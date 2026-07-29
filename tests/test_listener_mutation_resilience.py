#!/usr/bin/env python3
"""Live regression for listener survival during repeated Grasshopper mutations.

Enable the complete Scripts category in the Cassis tool panel first.
Run the test in a dedicated new document; it leaves its seven components for inspection.
"""

import argparse
import json
import re
import time
from concurrent.futures import ThreadPoolExecutor
from typing import Any, Dict, Iterable, List, Optional, Tuple

import requests


GUID = re.compile(
    r"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-"
    r"[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b"
)


def nested_values(value: Any) -> Iterable[Any]:
    yield value
    if isinstance(value, dict):
        for child in value.values():
            yield from nested_values(child)
    elif isinstance(value, list):
        for child in value:
            yield from nested_values(child)
    elif isinstance(value, str):
        try:
            parsed = json.loads(value)
        except json.JSONDecodeError:
            return
        yield from nested_values(parsed)


def normalized(name: str) -> str:
    return re.sub(r"[^a-z0-9]", "", name.lower())


class LiveClient:
    def __init__(self, url: str, timeout: int = 30):
        self.url = url
        self.timeout = timeout
        self.request_id = 0

    def request(
        self,
        method: str,
        params: Optional[Dict[str, Any]] = None,
    ) -> Tuple[Dict[str, Any], str]:
        self.request_id += 1
        response = requests.post(
            self.url,
            json={
                "jsonrpc": "2.0",
                "id": self.request_id,
                "method": method,
                "params": params or {},
            },
            timeout=self.timeout,
        )
        response.raise_for_status()
        text = response.text.strip().lstrip("\ufeff")
        if text.startswith("data: "):
            text = text[6:].splitlines()[0]
        message = json.loads(text)
        if not isinstance(message, dict) or message.get("jsonrpc") != "2.0":
            raise AssertionError(f"Unstructured JSON-RPC response: {response.text[:500]}")
        if "error" in message:
            raise AssertionError(f"JSON-RPC error: {message['error']}")
        if "result" not in message:
            raise AssertionError(f"JSON-RPC response has no result: {message}")
        for item in nested_values(message["result"]):
            if isinstance(item, dict) and (item.get("isError") is True or item.get("IsError") is True):
                raise AssertionError(f"Tool returned an error: {item}")
            if isinstance(item, dict) and (item.get("success") is False or item.get("Success") is False):
                raise AssertionError(f"Mutation failed: {item}")
            if isinstance(item, dict) and item.get("ok") is False:
                raise AssertionError(f"Tool failed: {item}")
        return message, response.text

    def call_tool(self, name: str, arguments: Dict[str, Any]) -> Tuple[Dict[str, Any], str]:
        return self.request("tools/call", {"name": name, "arguments": arguments})


def server_version(client: LiveClient) -> str:
    message, _ = client.request(
        "initialize",
        {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": {"name": "cassis-listener-regression", "version": "1.0.0"},
        },
    )
    for item in nested_values(message["result"]):
        if isinstance(item, dict) and isinstance(item.get("serverInfo"), dict):
            return str(item["serverInfo"].get("version", ""))
    raise AssertionError("Initialize response has no serverInfo.version")


def resolve_tools(client: LiveClient) -> Dict[str, str]:
    message, _ = client.request("tools/list")
    names = []
    for item in nested_values(message["result"]):
        if isinstance(item, dict) and isinstance(item.get("tools"), list):
            names.extend(tool.get("name") for tool in item["tools"] if isinstance(tool, dict))
    return {normalized(name): name for name in names if isinstance(name, str)}


def require_tools(available: Dict[str, str], requested: Iterable[str]) -> Dict[str, str]:
    resolved = {}
    for name in requested:
        key = normalized(name)
        if key not in available:
            raise AssertionError(
                f"Required tool is not enabled: {name}. "
                "Enable the complete Scripts category in the Cassis tool panel."
            )
        resolved[name] = available[key]
    return resolved


def component_id(message: Dict[str, Any]) -> str:
    for item in nested_values(message["result"]):
        if isinstance(item, str):
            match = GUID.search(item)
            if match:
                return match.group(0)
    raise AssertionError(f"Component GUID missing from response: {message}")


def parameter_index(message: Dict[str, Any]) -> int:
    for item in nested_values(message["result"]):
        if isinstance(item, dict) and isinstance(item.get("index"), int):
            return item["index"]
    raise AssertionError(f"Parameter index missing from response: {message}")


def concurrent_edits(url: str, tool: str, component_ids: List[str], wave: int) -> None:
    def edit(client_index: int) -> None:
        client = LiveClient(url)
        client.call_tool(
            tool,
            {
                "componentId": component_ids[client_index],
                "code": f"a = {wave * 10 + client_index};",
            },
        )

    with ThreadPoolExecutor(max_workers=4) as executor:
        futures = [executor.submit(edit, index) for index in range(4)]
        for future in futures:
            future.result()


def run(args: argparse.Namespace) -> None:
    client = LiveClient(args.url)
    version = server_version(client)
    if version != args.expected_version:
        raise AssertionError(f"Expected Cassis {args.expected_version}, listener reports {version}")

    tools = require_tools(
        resolve_tools(client),
        [
            "addcsharpscriptcomponent",
            "add_script_parameter",
            "remove_script_parameter",
            "modify_script_component_parameters",
            "edit_csharp_script",
        ],
    )

    component_ids: List[str] = []
    sequential_requests = 0
    try:
        for index in range(7):
            created, _ = client.call_tool(
                tools["addcsharpscriptcomponent"],
                {"x": 150 + (index % 4) * 220, "y": 150 + (index // 4) * 180},
            )
            component_ids.append(component_id(created))

        for cycle in range(args.cycles):
            for index, component in enumerate(component_ids):
                added, _ = client.call_tool(
                    tools["add_script_parameter"],
                    {
                        "componentId": component,
                        "side": "input",
                        "name": f"stress_{cycle}_{index}",
                        "description": "Listener resilience regression parameter",
                        "type": "Number",
                        "access": "item",
                    },
                )
                added_index = parameter_index(added)
                sequential_requests += 1
                time.sleep(args.delay)

                client.call_tool(
                    tools["modify_script_component_parameters"],
                    {
                        "componentId": component,
                        "parameterConfig": {
                            "input_0": f"value_{cycle}_{index}",
                            "input_0:type": "Number",
                            "input_0:access": "item",
                        },
                    },
                )
                sequential_requests += 1
                time.sleep(args.delay)

                client.call_tool(
                    tools["edit_csharp_script"],
                    {"componentId": component, "code": f"a = {cycle * 100 + index};"},
                )
                sequential_requests += 1
                time.sleep(args.delay)

                client.call_tool(
                    tools["remove_script_parameter"],
                    {"componentId": component, "side": "input", "index": added_index},
                )
                sequential_requests += 1
                time.sleep(args.delay)

        for wave in range(4):
            concurrent_edits(args.url, tools["edit_csharp_script"], component_ids, wave)

        client.request("tools/list")
        final_version = server_version(client)
        if final_version != args.expected_version:
            raise AssertionError(f"Listener version changed after stress: {final_version}")

        print(
            f"PASS: Cassis {final_version}; {sequential_requests} sequential mutations "
            "across 7 components; 16 mutations from 4 concurrent clients; listener reachable."
        )
    finally:
        if component_ids:
            print(f"Test components left in the active document: {', '.join(component_ids)}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://localhost:3003/mcp/")
    parser.add_argument("--expected-version", default="1.3.4")
    parser.add_argument("--cycles", type=int, default=5, help="Five cycles produce 140 sequential mutations.")
    parser.add_argument("--delay", type=float, default=0.25)
    run(parser.parse_args())


if __name__ == "__main__":
    main()
