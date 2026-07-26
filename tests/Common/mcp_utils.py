#!/usr/bin/env python3
"""
Common utilities for Cassis tests.
Provides shared functionality for MCP communication and response parsing.
"""

import os
import json
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
import requests
from typing import Dict, Any, Optional, Tuple


class MCPClient:
    """Client for communicating with GrasshopperMCP server."""
    
    def __init__(self, url: str = None, timeout: int = 10):
        self.url = url or os.getenv('MCP_URL', 'http://localhost:3003/mcp/')
        self.timeout = timeout
    
    def clean_response(self, text: str) -> str:
        """Clean MCP response text (remove BOM and handle SSE format)."""
        # Remove BOM characters more thoroughly
        if text.startswith('ï»¿'):
            text = text[3:]
        elif text.startswith('\ufeff'):
            text = text[1:]
        elif text.startswith('\xef\xbb\xbf'):
            text = text[3:]
        
        # Handle Server-Sent Events (SSE) format
        if text.startswith('data: '):
            text = text.split('data: ')[1].split('\n')[0]
        
        # Remove any trailing whitespace or newlines
        text = text.strip()
        
        return text
    
    def extract_component_data(self, response_text: str) -> Optional[Dict[str, Any]]:
        """Extract component data from nested MCP response."""
        try:
            cleaned = self.clean_response(response_text)
            mcp_response = json.loads(cleaned)
            
            # Handle different nesting structures
            if 'result' in mcp_response and 'content' in mcp_response['result']:
                outer_content = mcp_response['result']['content'][0]['text']
                inner_data = json.loads(outer_content)
                
                # Case 1: Triple-nested (older structure)
                if 'content' in inner_data:
                    inner_content = inner_data['content'][0]['text']
                    component_data = json.loads(inner_content)
                    return component_data
                # Case 2: Direct data (newer structure)
                elif 'Success' in inner_data or 'OverallStatus' in inner_data:
                    return inner_data
                    
            # Handle direct tool list response
            elif 'result' in mcp_response and 'tools' in mcp_response['result']:
                return mcp_response['result']
                
        except Exception as e:
            print(f"   Error parsing response: {e}")
            print(f"   Raw response start: {repr(response_text[:100])}")
            return None
        
        return None
    
    def make_request(self, method: str, params: Dict[str, Any] = None) -> Tuple[bool, str]:
        """Make an MCP request and return success status and response text."""
        try:
            payload = {
                'jsonrpc': '2.0',
                'id': 1,
                'method': method,
                'params': params or {}
            }
            
            response = requests.post(self.url, json=payload, timeout=self.timeout)
            return response.status_code == 200, response.text
            
        except requests.exceptions.RequestException as e:
            return False, str(e)
    
    def call_tool(self, tool_name: str, arguments: Dict[str, Any] = None) -> Tuple[bool, str]:
        """Call a specific MCP tool."""
        return self.make_request('tools/call', {
            'name': tool_name,
            'arguments': arguments or {}
        })
    
    def list_tools(self) -> Tuple[bool, str]:
        """List available MCP tools."""
        return self.make_request('tools/list')
    
    def test_connectivity(self) -> bool:
        """Test basic MCP connectivity."""
        success, response = self.list_tools()
        if success:
            print("   ✓ MCP server responding")
            return True
        else:
            print(f"   ✗ Connection failed: {response}")
            return False

    def run_timeout_regression(
        self,
        tool_name: str = "getcomponentcount",
        timeout_seconds: int = 2
    ) -> Dict[str, Any]:
        """Verify requests terminate quickly under a strict timeout budget."""
        previous_timeout = self.timeout
        self.timeout = timeout_seconds
        start = time.monotonic()
        try:
            success, response = self.call_tool(tool_name, {})
        finally:
            self.timeout = previous_timeout

        elapsed_seconds = time.monotonic() - start
        return {
            "scenario": "timeout_regression",
            "tool": tool_name,
            "success": success,
            "elapsed_seconds": round(elapsed_seconds, 3),
            "response": response
        }

    def run_concurrency_regression(
        self,
        tool_name: str = "getcomponentcount",
        requests_count: int = 8,
        max_workers: int = 4
    ) -> Dict[str, Any]:
        """Run a concurrent burst and return success/failure counts."""
        payload = {
            "jsonrpc": "2.0",
            "id": int(time.time() * 1000),
            "method": "tools/call",
            "params": {"name": tool_name, "arguments": {}}
        }

        def _single_call() -> bool:
            try:
                response = requests.post(
                    self.url,
                    json=payload,
                    timeout=(5, self.timeout)
                )
                return response.status_code == 200
            except requests.exceptions.RequestException:
                return False

        successes = 0
        failures = 0
        start = time.monotonic()
        with ThreadPoolExecutor(max_workers=max_workers) as executor:
            futures = [executor.submit(_single_call) for _ in range(requests_count)]
            for future in as_completed(futures):
                if future.result():
                    successes += 1
                else:
                    failures += 1

        elapsed_seconds = time.monotonic() - start
        return {
            "scenario": "concurrency_regression",
            "tool": tool_name,
            "requests": requests_count,
            "successes": successes,
            "failures": failures,
            "elapsed_seconds": round(elapsed_seconds, 3)
        }

    def run_restart_recovery_regression(
        self,
        tool_name: str = "getcomponentcount",
        attempts: int = 12,
        delay_seconds: float = 1.0
    ) -> Dict[str, Any]:
        """
        Poll repeatedly to validate recovery after a server restart.
        Caller can restart the MCP server while this probe runs.
        """
        outcomes = []
        for _ in range(attempts):
            success, response = self.call_tool(tool_name, {})
            outcomes.append({"success": success, "response": response})
            time.sleep(delay_seconds)

        return {
            "scenario": "restart_recovery_regression",
            "tool": tool_name,
            "attempts": attempts,
            "successes": sum(1 for item in outcomes if item["success"]),
            "failures": sum(1 for item in outcomes if not item["success"]),
            "outcomes": outcomes
        }


# Convenience functions for backward compatibility
def clean_response(text: str) -> str:
    """Clean MCP response text (remove BOM and handle SSE format)."""
    client = MCPClient()
    return client.clean_response(text)


def extract_component_data(response_text: str) -> Optional[Dict[str, Any]]:
    """Extract component data from nested MCP response."""
    client = MCPClient()
    return client.extract_component_data(response_text)


def make_mcp_request(method: str, params: Dict[str, Any] = None) -> Tuple[bool, str]:
    """Make an MCP request and return success status and response text."""
    client = MCPClient()
    return client.make_request(method, params)
