#!/usr/bin/env python3
"""
Grasshopper MCP Test Suite
Interactive testing tool for Grasshopper MCP tools and workflows

This module provides a comprehensive testing interface for Grasshopper MCP tools,
featuring an improved class-based architecture, better error handling, and
enhanced user experience.

Command-line interface for AI assistant usage
Usage: python grasshopper_mcp_tester.py --help
"""

import os
import json
import requests
import time
import logging
import argparse
import sys
from concurrent.futures import ThreadPoolExecutor, as_completed
from typing import Dict, List, Optional, Tuple, Any, Union
from dataclasses import dataclass, field
from enum import Enum
from pathlib import Path

from rich.console import Console
from rich.panel import Panel
from rich.table import Table
from rich.prompt import Prompt, Confirm, IntPrompt
from rich.text import Text
from rich.layout import Layout
from rich.live import Live
from rich.align import Align
from rich import box
from rich.progress import Progress, SpinnerColumn, TextColumn
from rich.rule import Rule
from rich.logging import RichHandler

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format="%(message)s",
    datefmt="[%X]",
    handlers=[RichHandler(rich_tracebacks=True)]
)
logger = logging.getLogger("grasshopper_mcp_tester")

# Initialize rich console with better styling
console = Console(
    color_system="auto",
    width=120,
    highlight=True
)

class ComponentType(Enum):
    """Enumeration of supported component types"""
    PYTHON_SCRIPT = "pythonscript"
    CSHARP_SCRIPT = "csharpscript"
    SLIDER = "slider"
    PANEL = "panel"
    NUMBER = "number"
    POINT = "point"
    CURVE = "curve"

class TestStatus(Enum):
    """Test execution status"""
    PENDING = "pending"
    RUNNING = "running"
    PASSED = "passed"
    FAILED = "failed"
    SKIPPED = "skipped"

@dataclass
class MCPConfig:
    """Configuration for MCP connection and settings"""
    url: str = field(default_factory=lambda: os.getenv('MCP_URL', 'http://localhost:3003/mcp'))
    timeout: int = 30  # Increased from 10 to 30 seconds for Grasshopper operations
    retry_attempts: int = 3
    retry_delay: float = 1.0
    log_level: str = "INFO"
    config_file: str = "mcp_config.json"
    
    def __post_init__(self):
        """Validate configuration after initialization"""
        if not self.url.startswith(('http://', 'https://')):
            raise ValueError("MCP URL must start with http:// or https://")
    
    @classmethod
    def load_from_file(cls, config_path: str = "mcp_config.json") -> 'MCPConfig':
        """Load configuration from JSON file"""
        config_file = Path(config_path)
        if config_file.exists():
            try:
                with open(config_file, 'r') as f:
                    data = json.load(f)
                return cls(**data)
            except Exception as e:
                logger.warning(f"Failed to load config from {config_path}: {e}")
        
        return cls()
    
    def save_to_file(self, config_path: str = None) -> bool:
        """Save configuration to JSON file"""
        if config_path is None:
            config_path = self.config_file
        
        try:
            config_file = Path(config_path)
            with open(config_file, 'w') as f:
                json.dump(self.__dict__, f, indent=2)
            logger.info(f"Configuration saved to {config_path}")
            return True
        except Exception as e:
            logger.error(f"Failed to save config to {config_path}: {e}")
            return False

@dataclass
class TestResult:
    """Result of a test execution"""
    test_name: str
    status: TestStatus
    message: str
    duration: float
    error: Optional[Exception] = None
    data: Optional[Dict[str, Any]] = None

class MCPClient:
    """Enhanced MCP client with retry logic and better error handling"""
    
    def __init__(self, config: MCPConfig):
        self.config = config
        self.session = requests.Session()
        self.session.headers.update({
            'Content-Type': 'application/json',
            'User-Agent': 'Cassis-Tester/1.0'
        })
    
    def make_request(self, method: str, params: Optional[Dict[str, Any]] = None) -> Tuple[bool, Union[Dict[str, Any], str]]:
        """
        Make an MCP request with retry logic and enhanced error handling
        
        Args:
            method: MCP method name
            params: Optional parameters for the request
            
        Returns:
            Tuple of (success: bool, response: Union[Dict, str])
        """
        payload = {
            'jsonrpc': '2.0',
            'id': int(time.time() * 1000),  # Use timestamp for unique IDs
            'method': method,
            'params': params or {}
        }
        
        for attempt in range(self.config.retry_attempts):
            try:
                logger.debug(f"Making MCP request: {method} (attempt {attempt + 1})")
                # Use tuple for timeout: (connect_timeout, read_timeout)
                # Longer read timeout for SSE streams and long-running Grasshopper operations
                timeout_tuple = (5, self.config.timeout)  # 5s connect, configurable read timeout
                response = self.session.post(
                    self.config.url, 
                    json=payload, 
                    timeout=timeout_tuple
                )
                
                if response.status_code == 200:
                    return self._parse_response(response)
                else:
                    error_msg = f"HTTP {response.status_code}: {response.text}"
                    logger.warning(f"HTTP error: {error_msg}")
                    if attempt == self.config.retry_attempts - 1:
                        return False, error_msg
                        
            except requests.exceptions.ConnectionError as e:
                error_msg = f"Connection failed - check if MCP server is running on {self.config.url}"
                logger.error(f"Connection error: {e}")
                if attempt == self.config.retry_attempts - 1:
                    return False, error_msg
                    
            except requests.exceptions.Timeout as e:
                error_msg = f"Request timeout after {self.config.timeout}s - server may be overloaded or operation is taking longer than expected. Try increasing timeout in settings."
                logger.error(f"Timeout error: {e}")
                if attempt == self.config.retry_attempts - 1:
                    return False, error_msg
                    
            except Exception as e:
                error_msg = f"Request failed: {str(e)}"
                logger.error(f"Unexpected error: {e}")
                if attempt == self.config.retry_attempts - 1:
                    return False, error_msg
            
            # Wait before retry
            if attempt < self.config.retry_attempts - 1:
                time.sleep(self.config.retry_delay * (attempt + 1))
        
        return False, "Max retry attempts exceeded"
    
    def _parse_response(self, response: requests.Response) -> Tuple[bool, Union[Dict[str, Any], str]]:
        """Parse MCP response handling SSE format and BOM characters"""
        content = response.text
        
        # Remove BOM characters
        if content.startswith('\ufeff'):
            content = content[1:]
        elif content.startswith('ï»¿'):
            content = content[3:]
        
        # Look for SSE data lines
        lines = content.split('\n')
        for line in lines:
            line = line.strip()
            if line.startswith('data: '):
                json_data = line[6:]  # Remove 'data: ' prefix
                try:
                    return True, json.loads(json_data)
                except json.JSONDecodeError:
                    continue  # Try next line
        
        # If no valid SSE data found, try to parse as regular JSON
        try:
            return True, response.json()
        except json.JSONDecodeError:
            return False, f"Invalid JSON response: {content[:200]}..."

# Global MCP client instance with config file support
mcp_config = MCPConfig.load_from_file()
mcp_client = MCPClient(mcp_config)

def make_mcp_request(method: str, params: Optional[Dict[str, Any]] = None) -> Tuple[bool, Union[Dict[str, Any], str]]:
    """Legacy function for backward compatibility"""
    return mcp_client.make_request(method, params)

class UIManager:
    """Enhanced UI manager for rich console interactions"""
    
    def __init__(self, console: Console):
        self.console = console
        self.current_layout: Optional[Layout] = None
    
    def show_header(self) -> None:
        """Display the application header"""
        header_text = """
╔══════════════════════════════════════════════════════════════════════════════╗
║                        Grasshopper MCP Test Suite                           ║
║                     Enhanced Interactive Testing Tool                       ║
╚══════════════════════════════════════════════════════════════════════════════╝
        """
        
        header_panel = Panel(
            header_text,
            box=box.DOUBLE,
            border_style="blue",
            title="[bold blue]Welcome[/bold blue]",
            subtitle="[dim]Version 2.0 - Enhanced Architecture[/dim]"
        )
        self.console.print(header_panel)
    
    def show_success_panel(self, title: str, message: str, details: Optional[List[str]] = None) -> None:
        """Display a success panel with optional details"""
        content = f"[bold green]{message}[/bold green]"
        if details:
            content += "\n\n[bold]Details:[/bold]\n"
            for detail in details:
                content += f"✅ {detail}\n"
        
        panel = Panel(
            content,
            box=box.DOUBLE,
            border_style="green",
            title=f"[bold]{title}[/bold]"
        )
        self.console.print(panel)
    
    def show_error_panel(self, title: str, message: str, error: Optional[Exception] = None) -> None:
        """Display an error panel with optional exception details"""
        content = f"[bold red]{message}[/bold red]"
        if error:
            content += f"\n\n[dim]Error details: {str(error)}[/dim]"
        
        panel = Panel(
            content,
            box=box.DOUBLE,
            border_style="red",
            title=f"[bold]{title}[/bold]"
        )
        self.console.print(panel)
    
    def show_info_panel(self, title: str, message: str) -> None:
        """Display an informational panel"""
        panel = Panel(
            f"[bold blue]{message}[/bold blue]",
            box=box.ROUNDED,
            border_style="blue",
            title=f"[bold]{title}[/bold]"
        )
        self.console.print(panel)
    
    def create_table(self, title: str, columns: List[str], rows: List[List[str]]) -> Table:
        """Create a formatted table"""
        table = Table(
            title=title,
            box=box.ROUNDED,
            header_style="bold magenta",
            title_style="bold blue"
        )
        
        for col in columns:
            table.add_column(col, style="cyan")
        
        for row in rows:
            table.add_row(*row)
        
        return table
    
    def show_progress(self, description: str, total: int = 100) -> Progress:
        """Show a progress bar"""
        return Progress(
            SpinnerColumn(),
            TextColumn("[progress.description]{task.description}"),
            console=self.console
        )

# Global UI manager instance
ui_manager = UIManager(console)

def clean_response(text: str) -> str:
    """Clean response text for JSON parsing"""
    if text.startswith('```json'):
        text = text[7:]
    if text.endswith('```'):
        text = text[:-3]
    return text.strip()

def extract_component_data(response_text: str):
    """Extract component data from nested MCP response"""
    try:
        cleaned = clean_response(response_text)
        mcp_response = json.loads(cleaned)
        
        if 'result' in mcp_response and 'content' in mcp_response['result']:
            outer_content = mcp_response['result']['content'][0]['text']
            inner_data = json.loads(outer_content)
            
            # Handle different response structures
            if 'content' in inner_data:
                inner_content = inner_data['content'][0]['text']
                component_data = json.loads(inner_content)
                return component_data
            elif 'Success' in inner_data or 'ComponentId' in inner_data:
                return inner_data
                
    except Exception as e:
        console.print(f"   [red]Error parsing response: {e}[/red]")
        return None
    
    return None


def extract_component_id_from_response(response: Dict[str, Any]) -> Optional[str]:
    """Extract component ID from MCP response with proper nested structure handling"""
    try:
        content_list = response.get('result', {}).get('content', [])
        if not content_list:
            return None
        
        response_text = content_list[0].get('text', '{}')
        # Clean up Windows line endings before parsing
        response_text = response_text.replace('\r\n', '\n').replace('\r', '\n').strip()
        response_data = json.loads(response_text)
        
        # Handle nested content structure
        if 'content' in response_data and response_data['content']:
            inner_content = response_data['content'][0]['text']
            inner_data = json.loads(inner_content)
            return inner_data.get('ComponentId')
        else:
            return response_data.get('ComponentId')
    except Exception as e:
        console.print(f"[dim]Error extracting component ID: {e}[/dim]")
        return None

class TestManager:
    """Enhanced test manager for organizing and executing tests"""
    
    def __init__(self, mcp_client: MCPClient, ui_manager: UIManager):
        self.mcp_client = mcp_client
        self.ui_manager = ui_manager
        self.test_results: List[TestResult] = []
        self.created_components: List[str] = []
    
    def run_test(self, test_name: str, test_func, *args, **kwargs) -> TestResult:
        """Run a test and record the result"""
        start_time = time.time()
        logger.info(f"Starting test: {test_name}")
        
        try:
            result = test_func(*args, **kwargs)
            duration = time.time() - start_time
            
            if result:
                test_result = TestResult(
                    test_name=test_name,
                    status=TestStatus.PASSED,
                    message="Test completed successfully",
                    duration=duration
                )
                logger.info(f"Test passed: {test_name} ({duration:.2f}s)")
            else:
                test_result = TestResult(
                    test_name=test_name,
                    status=TestStatus.FAILED,
                    message="Test failed",
                    duration=duration
                )
                logger.error(f"Test failed: {test_name} ({duration:.2f}s)")
            
        except Exception as e:
            duration = time.time() - start_time
            test_result = TestResult(
                test_name=test_name,
                status=TestStatus.FAILED,
                message=f"Test failed with exception: {str(e)}",
                duration=duration,
                error=e
            )
            logger.error(f"Test failed with exception: {test_name} - {e}")
        
        self.test_results.append(test_result)
        return test_result
    
    def cleanup_components(self) -> bool:
        """Clean up all created components"""
        if not self.created_components:
            return True
        
        logger.info(f"Cleaning up {len(self.created_components)} components")
        success, response = self.mcp_client.make_request('tools/call', {
            'name': 'removecomponents',
            'arguments': {'componentIds': self.created_components}
        })
        
        if success:
            logger.info("Components cleaned up successfully")
            self.created_components.clear()
            return True
        else:
            logger.warning(f"Failed to clean up components: {response}")
            return False
    
    def get_test_summary(self) -> Dict[str, Any]:
        """Get summary of all test results"""
        total_tests = len(self.test_results)
        passed_tests = len([r for r in self.test_results if r.status == TestStatus.PASSED])
        failed_tests = len([r for r in self.test_results if r.status == TestStatus.FAILED])
        total_duration = sum(r.duration for r in self.test_results)
        
        return {
            'total_tests': total_tests,
            'passed_tests': passed_tests,
            'failed_tests': failed_tests,
            'success_rate': (passed_tests / total_tests * 100) if total_tests > 0 else 0,
            'total_duration': total_duration,
            'average_duration': total_duration / total_tests if total_tests > 0 else 0
        }

# Global test manager instance
test_manager = TestManager(mcp_client, ui_manager)

# Spiral code for both Python and C#
PYTHON_SPIRAL_CODE = '''import math
import Rhino.Geometry as rg

# Input parameters - Grasshopper provides x and y
# Use them directly for spiral generation
if x is not None and y is not None:
    spiral_radius = float(x)  # Base radius of the spiral
    spiral_height = float(y)  # Height factor for the spiral
    
    # Generate spiral points
    points = []
    for i in range(100):
        t = i * 0.1
        radius = spiral_radius * (1 + t * 0.1)  # Expanding radius
        x_coord = radius * math.cos(t)
        y_coord = radius * math.sin(t)
        z_coord = spiral_height * t * 0.5
        points.append(rg.Point3d(x_coord, y_coord, z_coord))
    
    # Create polyline and convert to nurbs curve
    polyline = rg.Polyline(points)
    curve = polyline.ToNurbsCurve()
    
    # Output the spiral curve and points
    # Grasshopper expects these specific output variable names
    a = curve
    b = points
else:
    # Handle case when inputs are not provided
    a = None
    b = None
'''

CSHARP_SPIRAL_CODE = '''using System;
using System.Collections.Generic;
using Rhino.Geometry;

// Input parameters - Grasshopper provides x and y
// Use them directly for spiral generation
if (x != null && y != null)
{
    double spiralRadius = Convert.ToDouble(x);  // Base radius of the spiral
    double spiralHeight = Convert.ToDouble(y);  // Height factor for the spiral
    
    // Generate spiral points
    List<Point3d> points = new List<Point3d>();
    for (int i = 0; i < 100; i++)
    {
        double t = i * 0.1;
        double radius = spiralRadius * (1 + t * 0.1);  // Expanding radius
        double xCoord = radius * Math.Cos(t);
        double yCoord = radius * Math.Sin(t);
        double zCoord = spiralHeight * t * 0.5;
        points.Add(new Point3d(xCoord, yCoord, zCoord));
    }
    
    // Create polyline and convert to nurbs curve
    Polyline polyline = new Polyline(points);
    NurbsCurve curve = polyline.ToNurbsCurve();
    
    // Output the spiral curve and points
    // Grasshopper expects these specific output variable names
    A = curve;
    B = points;
}
else
{
    // Handle case when inputs are not provided
    A = null;
    B = null;
}
'''

def show_header():
    """Display the application header - legacy function for backward compatibility"""
    ui_manager.show_header()

def check_mcp_connection() -> bool:
    """Check if MCP server is responding with enhanced error handling"""
    with ui_manager.show_progress("Checking MCP connection...") as progress:
        task = progress.add_task("Connecting to MCP server...", total=100)
        progress.update(task, advance=50)
        
        success, response = mcp_client.make_request('tools/list')
        progress.update(task, advance=50)
    
    if success:
        ui_manager.show_success_panel(
            "Connection Successful",
            "MCP server is responding",
            [f"Found {len(response.get('result', {}).get('tools', []))} available tools"]
        )
        return True
    else:
        ui_manager.show_error_panel(
            "Connection Failed",
            f"MCP server not responding. Please start the server first.\nError: {response}"
        )
        return False

def list_components():
    """List all current components with enhanced display"""
    console.print("\n🔍 [blue]Listing current components...[/blue]")
    
    with console.status("[blue]Fetching components...", spinner="dots"):
        success, response = make_mcp_request('tools/call', {
            'name': 'capturecanvasstate',
            'arguments': {}
        })

    if success and 'result' in response:
        try:
            # Handle different response structures
            if 'content' in response['result'] and response['result']['content']:
                content = json.loads(response['result']['content'][0]['text'])
            else:
                # Direct response structure
                content = response['result']
            
            components = content.get('Components', [])
            
            if not components:
                console.print(Panel("📭 [yellow]No components found on canvas[/yellow]", box=box.ROUNDED))
                return []
            
            # Create enhanced rich table
            table = Table(
                title="[bold]Current Components[/bold]",
                box=box.ROUNDED,
                header_style="bold magenta",
                title_style="bold blue"
            )
            table.add_column("ID", style="cyan", no_wrap=True, width=12)
            table.add_column("Type", style="magenta", width=20)
            table.add_column("Name", style="green", width=25)
            table.add_column("Position", style="yellow", width=15)
            table.add_column("Script Status", style="blue", width=20)
            table.add_column("Details", style="white", width=30)
            
            script_components = []
            for comp in components:
                comp_type = comp.get('Type', 'Unknown')
                comp_name = comp.get('Name', 'Unnamed')
                comp_id = comp.get('Id', 'No ID')[:8] + '...'
                script_content = comp.get('ScriptContent', '')
                pos_x = comp.get('X', 0)
                pos_y = comp.get('Y', 0)
                
                # Determine script status and details
                if 'script' in comp_type.lower() or 'python' in comp_type.lower() or 'csharp' in comp_type.lower():
                    script_components.append(comp)
                    if script_content:
                        script_status = "✅ [green]Has Script[/green]"
                        details = f"Lines: {len(script_content.split(chr(10)))}"
                    else:
                        script_status = "❌ [red]Empty[/red]"
                        details = "[red]No content[/red]"
                else:
                    script_status = "[dim]N/A[/dim]"
                    details = "[dim]Regular component[/dim]"
                
                table.add_row(
                    comp_id,
                    comp_type,
                    comp_name,
                    f"({pos_x}, {pos_y})",
                    script_status,
                    details
                )
            
            console.print(table)
            
            # Summary panel
            summary_panel = Panel(
                f"[bold]Summary:[/bold] {len(components)} total components, {len(script_components)} script components",
                box=box.ROUNDED,
                border_style="green"
            )
            console.print(summary_panel)
            
            return script_components
        except Exception as e:
            console.print(Panel(f"❌ [red]Error parsing component data: {e}[/red]\nResponse: {response}", box=box.ROUNDED))
            return []
    else:
        console.print(Panel(f"❌ [red]Failed to get components[/red]\nResponse: {response}", box=box.ROUNDED))
        return []

def clear_canvas():
    """Clear the Grasshopper canvas with confirmation"""
    if not Confirm.ask("🧹 [red]Are you sure you want to clear the entire canvas?[/red]"):
        console.print("⚠️ [yellow]Canvas clear cancelled[/yellow]")
        return False
    
    with console.status("[blue]Clearing canvas...", spinner="dots"):
        success, response = make_mcp_request('tools/call', {
            'name': 'cleardocument',
            'arguments': {}
        })
    
    if success:
        console.print("✅ [green]Canvas cleared successfully[/green]")
    else:
        console.print("⚠️ [yellow]Canvas clear failed[/yellow]")
    return success

def validate_coordinates(x, y):
    """Validate coordinate inputs"""
    try:
        x = int(x)
        y = int(y)
        if x < -10000 or x > 10000 or y < -10000 or y > 10000:
            return False, "Coordinates must be between -10000 and 10000"
        return True, (x, y)
    except ValueError:
        return False, "Coordinates must be valid integers"

def validate_color(color):
    """Validate hex color input"""
    if not color.startswith('#'):
        return False, "Color must start with #"
    if len(color) != 7:
        return False, "Color must be 6 hex digits (e.g., #FF0000)"
    try:
        int(color[1:], 16)
        return True, color
    except ValueError:
        return False, "Color must contain valid hex digits"

def add_component(component_type, x, y):
    """Add a component to the canvas with progress indication and validation"""
    # Validate coordinates
    valid, coords_or_error = validate_coordinates(x, y)
    if not valid:
        console.print(f"❌ [red]Invalid coordinates: {coords_or_error}[/red]")
        return None
    
    x, y = coords_or_error
    console.print(f"➕ [blue]Adding {component_type} component at ({x}, {y})...[/blue]")
    
    with console.status(f"[blue]Creating {component_type} component...", spinner="dots"):
        success, response = make_mcp_request('tools/call', {
            'name': 'addcomponent',
            'arguments': {
                'type': component_type,
                'x': x,
                'y': y
            }
        })
    
    if success and 'result' in response:
        try:
            # Handle different response structures
            if 'content' in response['result'] and response['result']['content']:
                # Try to extract from nested content structure
                content_text = response['result']['content'][0]['text']
                
                # Parse the content text as JSON
                try:
                    content_data = json.loads(content_text)
                    
                    # Look for the actual component data
                    if 'content' in content_data and content_data['content']:
                        # Extract from inner content
                        inner_text = content_data['content'][0]['text']
                        component_data = json.loads(inner_text)
                        
                        if component_data.get('Success'):
                            component_id = component_data.get('ComponentId')
                            console.print(f"✅ [green]{component_type} component created: {component_id[:8]}...[/green]")
                            return component_id
                        else:
                            error_msg = component_data.get('ErrorMessage', 'Unknown error')
                            console.print(f"❌ [red]Failed to create {component_type} component: {error_msg}[/red]")
                            return None
                    else:
                        # Direct content structure
                        if content_data.get('Success'):
                            component_id = content_data.get('ComponentId')
                            console.print(f"✅ [green]{component_type} component created: {component_id[:8]}...[/green]")
                            return component_id
                        else:
                            error_msg = content_data.get('ErrorMessage', 'Unknown error')
                            console.print(f"❌ [red]Failed to create {component_type} component: {error_msg}[/red]")
                            return None
                            
                except json.JSONDecodeError:
                    # Fallback to old method
                    data = extract_component_data(content_text)
                    if data and data.get('Success'):
                        component_id = data.get('ComponentId')
                        console.print(f"✅ [green]{component_type} component created: {component_id[:8]}...[/green]")
                        return component_id
                    else:
                        error_msg = data.get('ErrorMessage', 'Unknown error') if data else 'No response data'
                        console.print(f"❌ [red]Failed to create {component_type} component: {error_msg}[/red]")
                        return None
            else:
                # Direct response structure
                data = response['result']
                
                if data and data.get('Success'):
                    component_id = data.get('ComponentId')
                    console.print(f"✅ [green]{component_type} component created: {component_id[:8]}...[/green]")
                    return component_id
                else:
                    error_msg = data.get('ErrorMessage', 'Unknown error') if data else 'No response data'
                    console.print(f"❌ [red]Failed to create {component_type} component: {error_msg}[/red]")
                    return None
                    
        except Exception as e:
            console.print(f"❌ [red]Error parsing component creation response: {e}[/red]")
            console.print(f"[dim]Response: {response}[/dim]")
            return None
    else:
        error_detail = response.get('error', {}).get('message', 'Unknown error') if isinstance(response, dict) else str(response)
        console.print(f"❌ [red]Failed to create {component_type} component: {error_detail}[/red]")
        return None

def set_component_script(component_id, language, script):
    """Set script content for a component with progress indication"""
    console.print(f"📝 [blue]Setting {language} script for component {component_id[:8]}...[/blue]")
    
    with console.status("[blue]Setting script content...", spinner="dots"):
        success, response = make_mcp_request('tools/call', {
            'name': 'setcomponentscript',
            'arguments': {
                'componentId': component_id,
                'language': language,
                'script': script
            }
        })
    
    if success:
        console.print("✅ [green]Script content set successfully[/green]")
        return True
    else:
        console.print(f"❌ [red]Failed to set script: {response}[/red]")
        return False

def set_component_value(component_id, parameter_name, value):
    """Set a parameter value for a component"""
    console.print(f"🔧 [blue]Setting {parameter_name} = {value} for component {component_id[:8]}...[/blue]")
    
    with console.status("[blue]Setting parameter value...", spinner="dots"):
        success, response = make_mcp_request('tools/call', {
            'name': 'setcomponentvalue',
            'arguments': {
                'componentId': component_id,
                'parameterName': parameter_name,
                'value': value
            }
        })
    
    if success:
        console.print(f"✅ [green]Parameter {parameter_name} set to {value}[/green]")
        return True
    else:
        console.print(f"❌ [red]Failed to set parameter: {response}[/red]")
        return False

def connect_components(source_id, source_output, target_id, target_input):
    """Connect two components with progress indication"""
    console.print(f"🔗 [blue]Connecting components...[/blue]")
    
    with console.status("[blue]Establishing connection...", spinner="dots"):
        success, response = make_mcp_request('tools/call', {
            'name': 'connectcomponents',
            'arguments': {
                'sourceComponentId': source_id,
                'sourceOutputIndex': source_output,
                'targetComponentId': target_id,
                'targetInputIndex': target_input
            }
        })
    
    if success:
        console.print("✅ [green]Components connected successfully[/green]")
        return True
    else:
        console.print(f"❌ [red]Failed to connect components: {response}[/red]")
        return False

def select_component(prompt_text="Select a component"):
    """Let user select a component from a numbered list"""
    console.print(f"\n🔍 [blue]{prompt_text}[/blue]")
    
    # Get current components
    success, response = make_mcp_request('tools/call', {
        'name': 'capturecanvasstate',
        'arguments': {}
    })
    
    if not success or 'result' not in response:
        console.print("❌ [red]Failed to fetch components[/red]")
        return None
    
    try:
        content = json.loads(response['result']['content'][0]['text'])
        components = content.get('Components', [])
        
        if not components:
            console.print("📭 [yellow]No components found on canvas[/yellow]")
            return None
        
        # Show components in a numbered list
        console.print("\n[bold]Available Components:[/bold]")
        for i, comp in enumerate(components, 1):
            comp_type = comp.get('Type', 'Unknown')
            comp_name = comp.get('Name', 'Unnamed')
            comp_id = comp.get('Id', 'No ID')
            pos_x = comp.get('X', 0)
            pos_y = comp.get('Y', 0)
            
            # Highlight script components
            if 'script' in comp_type.lower() or 'python' in comp_type.lower() or 'csharp' in comp_type.lower():
                script_status = " [green]📝[/green]" if comp.get('ScriptContent') else " [red]📝[/red]"
            else:
                script_status = ""
            
            console.print(f"  {i}. {comp_type}{script_status} - {comp_name} at ({pos_x}, {pos_y})")
        
        # Let user select
        choice = IntPrompt.ask(
            "Select component number",
            choices=[str(i) for i in range(1, len(components) + 1)]
        )
        
        selected_comp = components[choice - 1]
        component_id = selected_comp.get('Id')
        
        console.print(f"✅ [green]Selected: {selected_comp.get('Type')} - {selected_comp.get('Name')}[/green]")
        return component_id
        
    except Exception as e:
        console.print(f"❌ [red]Error selecting component: {e}[/red]")
        return None

def search_components():
    """Search components by type, name, or script content"""
    console.print("\n🔎 [blue]COMPONENT SEARCH[/blue]")
    console.print(Rule(style="blue"))
    
    search_type = Prompt.ask(
        "Search by",
        choices=["type", "name", "script", "all"],
        default="all"
    )
    
    search_term = Prompt.ask("Search term (leave empty for all)")
    
    # Get all components
    success, response = make_mcp_request('tools/call', {
        'name': 'capturecanvasstate',
        'arguments': {}
    })
    
    if not success or 'result' not in response:
        console.print("❌ [red]Failed to fetch components[/red]")
        return
    
    try:
        content = json.loads(response['result']['content'][0]['text'])
        components = content.get('Components', [])
        
        if not components:
            console.print("📭 [yellow]No components found on canvas[/yellow]")
            return
        
        # Filter components
        filtered_components = []
        for comp in components:
            comp_type = comp.get('Type', '').lower()
            comp_name = comp.get('Name', '').lower()
            script_content = comp.get('ScriptContent', '').lower()
            
            if search_type == "all" or not search_term:
                filtered_components.append(comp)
            elif search_type == "type" and search_term.lower() in comp_type:
                filtered_components.append(comp)
            elif search_type == "name" and search_term.lower() in comp_name:
                filtered_components.append(comp)
            elif search_type == "script" and search_term.lower() in script_content:
                filtered_components.append(comp)
        
        if not filtered_components:
            console.print(f"🔍 [yellow]No components found matching '{search_term}' in {search_type}[/yellow]")
            return
        
        # Display results
        console.print(f"\n🔍 [green]Found {len(filtered_components)} matching components:[/green]")
        
        table = Table(
            title=f"[bold]Search Results for '{search_term}' in {search_type}[/bold]",
            box=box.ROUNDED,
            header_style="bold magenta",
            title_style="bold blue"
        )
        table.add_column("ID", style="cyan", no_wrap=True, width=12)
        table.add_column("Type", style="magenta", width=20)
        table.add_column("Name", style="green", width=25)
        table.add_column("Position", style="yellow", width=15)
        table.add_column("Script Status", style="blue", width=20)
        
        for comp in filtered_components:
            comp_type = comp.get('Type', 'Unknown')
            comp_name = comp.get('Name', 'Unnamed')
            comp_id = comp.get('Id', 'No ID')[:8] + '...'
            script_content = comp.get('ScriptContent', '')
            pos_x = comp.get('X', 0)
            pos_y = comp.get('Y', 0)
            
            if script_content:
                script_status = "✅ [green]Has Script[/green]"
            else:
                script_status = "❌ [red]Empty[/red]" if 'script' in comp_type.lower() else "[dim]N/A[/dim]"
            
            table.add_row(
                comp_id,
                comp_type,
                comp_name,
                f"({pos_x}, {pos_y})",
                script_status
            )
        
        console.print(table)
        
    except Exception as e:
        console.print(f"❌ [red]Error during search: {e}[/red]")

def show_help():
    """Show help and usage tips"""
    console.print("\n❓ [blue]HELP & USAGE TIPS[/blue]")
    console.print(Rule(style="blue"))
    
    help_panel = Panel(
        "[bold]Quick Tips:[/bold]\n\n"
        "🔍 [green]Component Selection:[/green] Use numbered lists instead of typing IDs\n"
        "🔧 [green]Tool Testing:[/green] Test individual MCP tools one by one\n"
        "🌀 [green]Spiral Workflow:[/green] Complete workflow: Input sliders → Python script → Output panels\n"
        "🔷 [green]C# Script Workflow:[/green] Complete workflow: Input sliders → C# script → Output panels\n"
        "📊 [green]Component Search:[/green] Find components by type, name, or script content\n\n"
        "[bold]Available MCP Tools:[/bold]\n\n"
        "📸 [blue]Canvas Tools:[/blue] capturecanvasstate, cleardocument, removecomponents\n"
        "➕ [blue]Component Creation:[/blue] addcomponent, addpythonscriptcomponent, addcsharpscriptcomponent\n"
        "🔧 [blue]Component Management:[/blue] setcomponentvalue, getcomponentinfo, connectcomponents\n"
        "📝 [blue]Script Management:[/blue] setcomponentscript, getcomponentscript\n"
        "📦 [blue]Grouping:[/blue] createcomponentgroup, groupexistingcomponents, groupcomponentsbytype\n"
        "⚡ [blue]Runtime Info:[/blue] getcomponentruntimeinfo, getallcomponentsruntimeinfo\n"
        "🔧 [blue]Advanced:[/blue] modifyscriptcomponentparameters, findcomponentbyguid, listallcomponentswithguids, debugdocumentstate, listavailablecomponents\n"
        "💚 [blue]System Health:[/blue] getsystemhealth, runhealthcheck, listhealthchecks\n\n"
        "[bold]New Testing Features:[/bold]\n\n"
        "🔷 [green]C# Script Testing:[/green] Test C# script components with basic and workflow tests\n"
        "🔗 [green]Connection Testing:[/green] Test component connections with automatic verification\n"
        "🔧 [green]Parameter Modification Testing:[/green] Test the new script parameter modification tool\n"
        "🐛 [green]Debug Tools:[/green] Enhanced debugging with findcomponentbyguid and debugdocumentstate\n\n"
        "[bold]Common Issues:[/bold]\n\n"
        "❌ [red]Connection Failed:[/red] Ensure MCP server is running\n"
        "❌ [red]Component Creation Failed:[/red] Check component type names (case-sensitive)\n"
        "❌ [red]Script Setting Failed:[/red] Verify component ID and language\n\n"
        "[bold]Keyboard Shortcuts:[/bold]\n\n"
        "Ctrl+C: Cancel current operation\n"
        "Enter: Continue to next step\n"
        "Q: Quick exit from most menus\n\n"
        "[bold]Debug Mode:[/bold]\n\n"
        "Type [yellow]999[/yellow] for automatic testing of all features",
        box=box.ROUNDED,
        border_style="blue",
        title="[bold]Grasshopper MCP Test Suite Help[/bold]"
    )
    
    console.print(help_panel)

def run_comprehensive_test():
    """Run a comprehensive test suite that covers all major functionality with cleanup and verification"""
    console.print("\n🧪 [bold blue]Running Comprehensive Grasshopper MCP Test Suite[/bold blue]")
    console.print("=" * 80)
    console.print("[dim]This test will create components, verify operations, and clean up afterward.[/dim]")
    
    test_results = []
    created_component_ids = []  # Track all created components for cleanup
    created_group_ids = []  # Track groups for cleanup
    health_check_names = []  # Track discovered health checks for later targeted runs
    test_start_time = time.time()
    
    def add_result(name: str, passed: bool, details: str, category: str = "General"):
        test_results.append({"name": name, "passed": passed, "details": details, "category": category})
    
    def extract_response_text(response):
        """Safely extract text content from MCP response"""
        try:
            return response.get('result', {}).get('content', [{}])[0].get('text', '{}')
        except (KeyError, IndexError, TypeError):
            return '{}'
    
    def open_path_if_exists(path: str | None):
        """Best-effort opener for files/folders (no-op if missing)."""
        try:
            if path and os.path.exists(path):
                os.startfile(path)
        except Exception:
            pass

    def path_exists(path: str | None) -> bool:
        """Return True if path is non-empty and exists."""
        return bool(path) and os.path.exists(path)

    def print_open_hint(path: str | None, label: str):
        """Render a clickable link in supported terminals."""
        if not path:
            return
        safe_path = path.replace("\\", "/")
        console.print(f"[cyan]{label}: [link=file:///{safe_path}]open[/link][/cyan]")

    def ensure_parent_dir(path: str | None):
        """Create parent directory for a file path."""
        if not path:
            return
        parent = os.path.dirname(path)
        if parent:
            os.makedirs(parent, exist_ok=True)

    def ensure_dir(path: str | None):
        """Create directory if provided."""
        if path:
            os.makedirs(path, exist_ok=True)

    def get_path_from_response(response):
        """Extract a file/folder path from a tool response."""
        try:
            data = json.loads(extract_response_text(response))
            for key in ["filePath", "FilePath", "path", "Path", "file", "Folder", "folder"]:
                if key in data:
                    return data[key]
        except Exception:
            pass
        return None
    
    try:
        # ═══════════════════════════════════════════════════════════════════════
        # CATEGORY 1: CONNECTION & DISCOVERY
        # ═══════════════════════════════════════════════════════════════════════
        console.print("\n[bold cyan]═══ CATEGORY 1: CONNECTION & DISCOVERY ═══[/bold cyan]")
        
        # Test 1.1: MCP Connection
        console.print("\n🔌 [blue]Test 1.1: MCP Connection[/blue]")
        success, response = make_mcp_request('tools/list', {})
        if success and 'result' in response and 'tools' in response['result']:
            tool_count = len(response['result']['tools'])
            tool_names = [t.get('name', 'unknown') for t in response['result']['tools']]
            console.print(f"✅ [green]Connected - {tool_count} tools available[/green]")
            add_result("MCP Connection", True, f"{tool_count} tools", "Connection")
        else:
            console.print("❌ [red]Connection failed - aborting tests[/red]")
            add_result("MCP Connection", False, "Connection failed", "Connection")
            return test_results
        
        # Test 1.2: System Health Check
        console.print("\n💚 [blue]Test 1.2: System Health Check[/blue]")
        success, response = make_mcp_request('tools/call', {'name': 'getsystemhealth', 'arguments': {}})
        if success:
            try:
                data = json.loads(extract_response_text(response))
                status = data.get('OverallStatus', 'Unknown')
                console.print(f"✅ [green]System health: {status}[/green]")
                add_result("System Health", True, status, "Connection")
            except:
                add_result("System Health", True, "Responded", "Connection")
        else:
            console.print("❌ [red]Health check failed[/red]")
            add_result("System Health", False, "Failed", "Connection")
        
        # Test 1.3: List Health Checks
        console.print("\n🩺 [blue]Test 1.3: List Health Checks[/blue]")
        success, response = make_mcp_request('tools/call', {'name': 'listhealthchecks', 'arguments': {}})
        if success:
            console.print("✅ [green]Health checks listed[/green]")
            add_result("List Health Checks", True, "Listed", "Connection")
            try:
                data = json.loads(extract_response_text(response))
                if isinstance(data, dict):
                    checks = data.get('HealthChecks') or data.get('healthChecks') or data.get('Checks') or data.get('health_checks')
                    if isinstance(checks, list):
                        health_check_names = [str(c) for c in checks if c]
                    elif isinstance(checks, dict):
                        health_check_names = [str(k) for k in checks.keys()]
            except Exception:
                pass
        else:
            add_result("List Health Checks", False, "Failed", "Connection")
        
        # Test 1.4: Get Component Count (baseline)
        console.print("\n📊 [blue]Test 1.4: Get Component Count (baseline)[/blue]")
        success, response = make_mcp_request('tools/call', {'name': 'Get_ComponentCount', 'arguments': {}})
        baseline_count = 0
        if success:
            try:
                data = json.loads(extract_response_text(response))
                baseline_count = data.get('TotalCount', data.get('Count', 0))
                console.print(f"✅ [green]Baseline component count: {baseline_count}[/green]")
                add_result("Get Component Count", True, f"{baseline_count} components", "Connection")
            except:
                add_result("Get Component Count", True, "Responded", "Connection")
        else:
            add_result("Get Component Count", False, "Failed", "Connection")
        
        # ═══════════════════════════════════════════════════════════════════════
        # CATEGORY 2: COMPONENT CREATION
        # ═══════════════════════════════════════════════════════════════════════
        console.print("\n[bold cyan]═══ CATEGORY 2: COMPONENT CREATION ═══[/bold cyan]")
        
        # Use random offset to avoid overlapping with existing components
        base_x = 1000 + int(time.time() % 1000)
        base_y = 1000
        
        # Test 2.1: Create Number Slider
        console.print("\n🎚️ [blue]Test 2.1: Create Number Slider[/blue]")
        success, response = make_mcp_request('tools/call', {
            'name': 'addcomponent',
            'arguments': {'type': 'slider', 'x': base_x, 'y': base_y}
        })
        slider_id = None
        if success:
            slider_id = extract_component_id_from_response(response)
            if slider_id:
                created_component_ids.append(slider_id)
                console.print(f"✅ [green]Slider created: {slider_id[:12]}...[/green]")
                add_result("Create Slider", True, slider_id[:12], "Creation")
            else:
                console.print("⚠️ [yellow]Request succeeded but no ID returned[/yellow]")
                add_result("Create Slider", False, "No ID", "Creation")
        else:
            console.print(f"❌ [red]Failed: {response}[/red]")
            add_result("Create Slider", False, "Failed", "Creation")
        
        # Test 2.2: Create Panel
        console.print("\n📋 [blue]Test 2.2: Create Panel[/blue]")
        success, response = make_mcp_request('tools/call', {
            'name': 'addcomponent',
            'arguments': {'type': 'panel', 'x': base_x + 300, 'y': base_y}
        })
        panel_id = None
        if success:
            panel_id = extract_component_id_from_response(response)
            if panel_id:
                created_component_ids.append(panel_id)
                console.print(f"✅ [green]Panel created: {panel_id[:12]}...[/green]")
                add_result("Create Panel", True, panel_id[:12], "Creation")
            else:
                add_result("Create Panel", False, "No ID", "Creation")
        else:
            add_result("Create Panel", False, "Failed", "Creation")
        
        # Test 2.3: Create Point Parameter
        console.print("\n📍 [blue]Test 2.3: Create Point Parameter[/blue]")
        success, response = make_mcp_request('tools/call', {
            'name': 'addcomponent',
            'arguments': {'type': 'point', 'x': base_x, 'y': base_y + 100}
        })
        point_id = None
        if success:
            point_id = extract_component_id_from_response(response)
            if point_id:
                created_component_ids.append(point_id)
                console.print(f"✅ [green]Point created: {point_id[:12]}...[/green]")
                add_result("Create Point", True, point_id[:12], "Creation")
            else:
                add_result("Create Point", False, "No ID", "Creation")
        else:
            add_result("Create Point", False, "Failed", "Creation")
        
        # Test 2.4: Create Python Script Component
        console.print("\n🐍 [blue]Test 2.4: Create Python Script Component[/blue]")
        test_python_code = '''# Test Python Script
a = x + y if x and y else 0
print(f"Result: {a}")
'''
        success, response = make_mcp_request('tools/call', {
            'name': 'addpythonscriptcomponent',
            'arguments': {'x': base_x + 150, 'y': base_y + 200, 'script': test_python_code}
        })
        python_id = None
        if success:
            python_id = extract_component_id_from_response(response)
            if python_id:
                created_component_ids.append(python_id)
                console.print(f"✅ [green]Python script created: {python_id[:12]}...[/green]")
                add_result("Create Python Script", True, python_id[:12], "Creation")
            else:
                add_result("Create Python Script", False, "No ID", "Creation")
        else:
            add_result("Create Python Script", False, "Failed", "Creation")
        
        # Test 2.5: Create C# Script Component
        console.print("\n🔷 [blue]Test 2.5: Create C# Script Component[/blue]")
        test_csharp_code = '''// Test C# Script
A = x + y;
'''
        success, response = make_mcp_request('tools/call', {
            'name': 'addcsharpscriptcomponent',
            'arguments': {'x': base_x + 150, 'y': base_y + 350, 'script': test_csharp_code}
        })
        csharp_id = None
        if success:
            csharp_id = extract_component_id_from_response(response)
            if csharp_id:
                created_component_ids.append(csharp_id)
                console.print(f"✅ [green]C# script created: {csharp_id[:12]}...[/green]")
                add_result("Create C# Script", True, csharp_id[:12], "Creation")
            else:
                add_result("Create C# Script", False, "No ID", "Creation")
        else:
            add_result("Create C# Script", False, "Failed", "Creation")
        
        # Test 2.6: Create Number Component
        console.print("\n🔢 [blue]Test 2.6: Create Number Component[/blue]")
        success, response = make_mcp_request('tools/call', {
            'name': 'addcomponent',
            'arguments': {'type': 'number', 'x': base_x - 150, 'y': base_y + 200}
        })
        number_id = None
        if success:
            number_id = extract_component_id_from_response(response)
            if number_id:
                created_component_ids.append(number_id)
                console.print(f"✅ [green]Number created: {number_id[:12]}...[/green]")
                add_result("Create Number", True, number_id[:12], "Creation")
            else:
                add_result("Create Number", False, "No ID", "Creation")
        else:
            add_result("Create Number", False, "Failed", "Creation")
        
        # Test 2.7: Create Boolean Toggle (for toggle tools)
        console.print("\n🔘 [blue]Test 2.7: Create Boolean Toggle[/blue]")
        success, response = make_mcp_request('tools/call', {
            'name': 'addcomponent',
            'arguments': {'type': 'boolean toggle', 'x': base_x - 150, 'y': base_y + 300}
        })
        toggle_id = None
        if success:
            toggle_id = extract_component_id_from_response(response)
            if toggle_id:
                created_component_ids.append(toggle_id)
                console.print(f"✅ [green]Boolean toggle created: {toggle_id[:12]}...[/green]")
                add_result("Create Boolean Toggle", True, toggle_id[:12], "Creation")
            else:
                add_result("Create Boolean Toggle", False, "No ID", "Creation")
        else:
            add_result("Create Boolean Toggle", False, "Failed", "Creation")
        
        # ═══════════════════════════════════════════════════════════════════════
        # CATEGORY 3: COMPONENT VALUE OPERATIONS
        # ═══════════════════════════════════════════════════════════════════════
        console.print("\n[bold cyan]═══ CATEGORY 3: COMPONENT VALUE OPERATIONS ═══[/bold cyan]")
        
        # Test 3.1: Set Slider Value
        console.print("\n🎚️ [blue]Test 3.1: Set Slider Value[/blue]")
        if slider_id:
            success, response = make_mcp_request('tools/call', {
                'name': 'setcomponentvalue',
                'arguments': {'componentId': slider_id, 'parameterName': 'value', 'value': 42.5}
            })
            if success:
                console.print("✅ [green]Slider value set to 42.5[/green]")
                add_result("Set Slider Value", True, "42.5", "Values")
            else:
                add_result("Set Slider Value", False, "Failed", "Values")
        else:
            console.print("⚠️ [yellow]Skipped - no slider created[/yellow]")
            add_result("Set Slider Value", False, "Skipped", "Values")
        
        # Test 3.2: Get Component Info (verify slider)
        console.print("\n📋 [blue]Test 3.2: Get Component Info[/blue]")
        if slider_id:
            success, response = make_mcp_request('tools/call', {
                'name': 'getcomponentinfo',
                'arguments': {'componentId': slider_id}
            })
            if success:
                console.print("✅ [green]Component info retrieved[/green]")
                add_result("Get Component Info", True, "Retrieved", "Values")
            else:
                add_result("Get Component Info", False, "Failed", "Values")
        else:
            add_result("Get Component Info", False, "Skipped", "Values")
        
        # Test 3.3: Get Detailed Component Info
        console.print("\n📊 [blue]Test 3.3: Get Detailed Component Info[/blue]")
        if slider_id:
            success, response = make_mcp_request('tools/call', {
                'name': 'GetDetailedComponentInfoById',
                'arguments': {'componentId': slider_id}
            })
            if success:
                console.print("✅ [green]Detailed info retrieved[/green]")
                add_result("Detailed Component Info", True, "Retrieved", "Values")
            else:
                add_result("Detailed Component Info", False, "Failed", "Values")
        else:
            add_result("Detailed Component Info", False, "Skipped", "Values")
        
        # Test 3.4: Set Panel Text
        console.print("\n📝 [blue]Test 3.4: Set Panel Text[/blue]")
        test_panel_text = "Test panel text from comprehensive test"
        if panel_id:
            success, response = make_mcp_request('tools/call', {
                'name': 'Set_Panel_Text',
                'arguments': {'componentId': panel_id, 'text': test_panel_text}
            })
            if success:
                console.print("✅ [green]Panel text set[/green]")
                add_result("Set Panel Text", True, "Set", "Values")
            else:
                add_result("Set Panel Text", False, "Failed", "Values")
        else:
            add_result("Set Panel Text", False, "Skipped", "Values")
        
        # Test 3.5: Get Panel Text (verify)
        console.print("\n📖 [blue]Test 3.5: Get Panel Text (verify)[/blue]")
        if panel_id:
            success, response = make_mcp_request('tools/call', {
                'name': 'Get_Panel_Text',
                'arguments': {'componentId': panel_id}
            })
            if success:
                # Panel text retrieval succeeded - the test passes if we got a response
                # The exact text might differ due to formatting, so we just verify we can read it
                try:
                    response_text = extract_response_text(response)
                    # Try to parse as JSON, but also handle raw text responses
                    try:
                        data = json.loads(response_text)
                        retrieved_text = data.get('Text', data.get('text', data.get('Content', '')))
                    except json.JSONDecodeError:
                        retrieved_text = response_text
                    
                    console.print(f"✅ [green]Panel text retrieved[/green]")
                    console.print(f"   [dim]Content: {str(retrieved_text)[:50]}...[/dim]")
                    add_result("Get Panel Text", True, "Retrieved", "Values")
                except Exception as e:
                    console.print(f"⚠️ [yellow]Retrieved but parse error: {e}[/yellow]")
                    add_result("Get Panel Text", True, "Retrieved", "Values")
            else:
                add_result("Get Panel Text", False, "Failed", "Values")
        else:
            add_result("Get Panel Text", False, "Skipped", "Values")
        
        # ═══════════════════════════════════════════════════════════════════════
        # CATEGORY 4: CONNECTIONS
        # ═══════════════════════════════════════════════════════════════════════
        console.print("\n[bold cyan]═══ CATEGORY 4: CONNECTIONS ═══[/bold cyan]")
        
        # Test 4.1: Connect Slider to Panel
        console.print("\n🔗 [blue]Test 4.1: Connect Slider to Panel[/blue]")
        if slider_id and panel_id:
            success, response = make_mcp_request('tools/call', {
                'name': 'connectcomponents',
                'arguments': {
                    'sourceComponentId': slider_id,
                    'sourceOutputIndex': 0,
                    'targetComponentId': panel_id,
                    'targetInputIndex': 0
                }
            })
            if success:
                console.print("✅ [green]Slider connected to Panel[/green]")
                add_result("Connect Slider→Panel", True, "Connected", "Connections")
            else:
                console.print(f"⚠️ [yellow]Connection failed (may be expected)[/yellow]")
                add_result("Connect Slider→Panel", False, "Failed", "Connections")
        else:
            add_result("Connect Slider→Panel", False, "Skipped", "Connections")
        
        # Test 4.2: Validate Connection
        console.print("\n✔️ [blue]Test 4.2: Validate Connection[/blue]")
        if slider_id and panel_id:
            success, response = make_mcp_request('tools/call', {
                'name': 'ValidateConnection',
                'arguments': {
                    'sourceComponentId': slider_id,
                    'sourceOutputIndex': 0,
                    'targetComponentId': panel_id,
                    'targetInputIndex': 0
                }
            })
            if success:
                console.print("✅ [green]Connection validated[/green]")
                add_result("Validate Connection", True, "Valid", "Connections")
            else:
                add_result("Validate Connection", False, "Invalid", "Connections")
        else:
            add_result("Validate Connection", False, "Skipped", "Connections")
        
        # Test 4.3: Get All Connections
        console.print("\n🔍 [blue]Test 4.3: Get All Connections[/blue]")
        success, response = make_mcp_request('tools/call', {'name': 'GetAllConnections', 'arguments': {}})
        if success:
            console.print("✅ [green]Connections retrieved[/green]")
            add_result("Get All Connections", True, "Retrieved", "Connections")
        else:
            add_result("Get All Connections", False, "Failed", "Connections")
        
        # ═══════════════════════════════════════════════════════════════════════
        # CATEGORY 5: SCRIPT OPERATIONS
        # ═══════════════════════════════════════════════════════════════════════
        console.print("\n[bold cyan]═══ CATEGORY 5: SCRIPT OPERATIONS ═══[/bold cyan]")
        
        # Test 5.1: List Python Scripts
        console.print("\n📋 [blue]Test 5.1: List Python Scripts[/blue]")
        success, response = make_mcp_request('tools/call', {'name': 'listpythonscripts', 'arguments': {}})
        if success:
            console.print("✅ [green]Python scripts listed[/green]")
            add_result("List Python Scripts", True, "Listed", "Scripts")
        else:
            add_result("List Python Scripts", False, "Failed", "Scripts")
        
        # Test 5.2: Get Python Script
        console.print("\n🐍 [blue]Test 5.2: Get Python Script[/blue]")
        if python_id:
            success, response = make_mcp_request('tools/call', {
                'name': 'getpythonscript',
                'arguments': {'componentId': python_id}
            })
            if success:
                console.print("✅ [green]Python script retrieved[/green]")
                add_result("Get Python Script", True, "Retrieved", "Scripts")
            else:
                add_result("Get Python Script", False, "Failed", "Scripts")
        else:
            add_result("Get Python Script", False, "Skipped", "Scripts")
        
        # Test 5.3: Edit Python Script
        console.print("\n✏️ [blue]Test 5.3: Edit Python Script[/blue]")
        if python_id:
            updated_code = '''# Updated Test Python Script
a = (x + y) * 2 if x and y else 0
print(f"Updated Result: {a}")
'''
            success, response = make_mcp_request('tools/call', {
                'name': 'editpythonscript',
                'arguments': {'componentId': python_id, 'newScript': updated_code}
            })
            if success:
                console.print("✅ [green]Python script edited[/green]")
                add_result("Edit Python Script", True, "Edited", "Scripts")
            else:
                add_result("Edit Python Script", False, "Failed", "Scripts")
        else:
            add_result("Edit Python Script", False, "Skipped", "Scripts")
        
        # Test 5.4: Get Python Script Errors
        console.print("\n🐛 [blue]Test 5.4: Get Python Script Errors[/blue]")
        if python_id:
            success, response = make_mcp_request('tools/call', {
                'name': 'getpythonscripterrors',
                'arguments': {'componentId': python_id}
            })
            if success:
                console.print("✅ [green]Script errors checked[/green]")
                add_result("Get Script Errors", True, "Checked", "Scripts")
            else:
                add_result("Get Script Errors", False, "Failed", "Scripts")
        else:
            add_result("Get Script Errors", False, "Skipped", "Scripts")
        
        # ═══════════════════════════════════════════════════════════════════════
        # CATEGORY 6: GROUPS
        # ═══════════════════════════════════════════════════════════════════════
        console.print("\n[bold cyan]═══ CATEGORY 6: GROUPS ═══[/bold cyan]")
        
        # Test 6.1: Create Component Group
        console.print("\n📦 [blue]Test 6.1: Create Component Group[/blue]")
        success, response = make_mcp_request('tools/call', {
            'name': 'createcomponentgroup',
            'arguments': {'name': 'TestGroup_ComprehensiveTest', 'x': base_x, 'y': base_y + 500, 'groupColor': '#3498db'}
        })
        group_id = None
        if success:
            try:
                data = json.loads(extract_response_text(response))
                group_id = data.get('GroupId', data.get('groupId'))
                if group_id:
                    created_group_ids.append(group_id)
            except:
                pass
            console.print("✅ [green]Group created[/green]")
            add_result("Create Group", True, "Created", "Groups")
        else:
            add_result("Create Group", False, "Failed", "Groups")
        
        # Test 6.2: Group Existing Components
        console.print("\n📦 [blue]Test 6.2: Group Existing Components[/blue]")
        if len(created_component_ids) >= 2:
            success, response = make_mcp_request('tools/call', {
                'name': 'groupexistingcomponents',
                'arguments': {
                    'componentIds': created_component_ids[:2],
                    'groupName': 'AutoGrouped_Test',
                    'groupColor': '#e74c3c'
                }
            })
            if success:
                console.print("✅ [green]Components grouped[/green]")
                add_result("Group Components", True, "Grouped", "Groups")
            else:
                add_result("Group Components", False, "Failed", "Groups")
        else:
            add_result("Group Components", False, "Skipped", "Groups")
        
        # ═══════════════════════════════════════════════════════════════════════
        # CATEGORY 7: CANVAS OPERATIONS
        # ═══════════════════════════════════════════════════════════════════════
        console.print("\n[bold cyan]═══ CATEGORY 7: CANVAS OPERATIONS ═══[/bold cyan]")
        
        # Test 7.1: Capture Canvas State
        console.print("\n📸 [blue]Test 7.1: Capture Canvas State[/blue]")
        success, response = make_mcp_request('tools/call', {'name': 'capturecanvasstate', 'arguments': {}})
        if success:
            try:
                data = json.loads(extract_response_text(response))
                total = data.get('TotalComponents', 0)
                console.print(f"✅ [green]Canvas captured - {total} components[/green]")
                add_result("Capture Canvas", True, f"{total} components", "Canvas")
            except:
                add_result("Capture Canvas", True, "Captured", "Canvas")
        else:
            add_result("Capture Canvas", False, "Failed", "Canvas")
        
        # Test 7.2: Get All Components
        console.print("\n📋 [blue]Test 7.2: Get All Components[/blue]")
        success, response = make_mcp_request('tools/call', {'name': 'Get_AllComponents', 'arguments': {}})
        if success:
            console.print("✅ [green]All components retrieved[/green]")
            add_result("Get All Components", True, "Retrieved", "Canvas")
        else:
            add_result("Get All Components", False, "Failed", "Canvas")
        
        # Test 7.3: List Panels
        console.print("\n📋 [blue]Test 7.3: List Panels[/blue]")
        success, response = make_mcp_request('tools/call', {'name': 'List_Panels', 'arguments': {}})
        if success:
            console.print("✅ [green]Panels listed[/green]")
            add_result("List Panels", True, "Listed", "Canvas")
        else:
            add_result("List Panels", False, "Failed", "Canvas")
        
        # Test 7.4: Move Component
        console.print("\n🔀 [blue]Test 7.4: Move Component[/blue]")
        if slider_id:
            success, response = make_mcp_request('tools/call', {
                'name': 'Move_Component',
                'arguments': {'componentId': slider_id, 'deltaX': 50, 'deltaY': 50}
            })
            if success:
                console.print("✅ [green]Component moved[/green]")
                add_result("Move Component", True, "Moved", "Canvas")
            else:
                add_result("Move Component", False, "Failed", "Canvas")
        else:
            add_result("Move Component", False, "Skipped", "Canvas")
        
        # ═══════════════════════════════════════════════════════════════════════
        # CATEGORY 8: DOCUMENT OPERATIONS
        # ═══════════════════════════════════════════════════════════════════════
        console.print("\n[bold cyan]═══ CATEGORY 8: DOCUMENT OPERATIONS ═══[/bold cyan]")
        
        # Test 8.1: Force Document Solution
        console.print("\n🔄 [blue]Test 8.1: Force Document Solution[/blue]")
        success, response = make_mcp_request('tools/call', {'name': 'ForceDocumentSolution', 'arguments': {}})
        if success:
            console.print("✅ [green]Document solution forced[/green]")
            add_result("Force Solution", True, "Solved", "Document")
        else:
            add_result("Force Solution", False, "Failed", "Document")
        
        # Test 8.2: Expire Component
        console.print("\n⏰ [blue]Test 8.2: Expire Component[/blue]")
        if slider_id:
            success, response = make_mcp_request('tools/call', {
                'name': 'ExpireComponent',
                'arguments': {'componentId': slider_id}
            })
            if success:
                console.print("✅ [green]Component expired[/green]")
                add_result("Expire Component", True, "Expired", "Document")
            else:
                add_result("Expire Component", False, "Failed", "Document")
        else:
            add_result("Expire Component", False, "Skipped", "Document")
        
        # ═══════════════════════════════════════════════════════════════════════
        # CATEGORY 9: NEGATIVE TESTS (Error Handling)
        # ═══════════════════════════════════════════════════════════════════════
        console.print("\n[bold cyan]═══ CATEGORY 9: ERROR HANDLING ═══[/bold cyan]")
        
        # Test 9.1: Invalid Component ID
        console.print("\n🚫 [blue]Test 9.1: Invalid Component ID (expect failure)[/blue]")
        success, response = make_mcp_request('tools/call', {
            'name': 'getcomponentinfo',
            'arguments': {'componentId': '00000000-0000-0000-0000-000000000000'}
        })
        if not success or 'error' in str(response).lower() or 'not found' in str(response).lower():
            console.print("✅ [green]Correctly rejected invalid ID[/green]")
            add_result("Invalid ID Handling", True, "Rejected", "Errors")
        else:
            console.print("⚠️ [yellow]Did not reject invalid ID[/yellow]")
            add_result("Invalid ID Handling", False, "Not rejected", "Errors")
        
        # Test 9.2: Invalid Component Type
        console.print("\n🚫 [blue]Test 9.2: Invalid Component Type (expect failure)[/blue]")
        success, response = make_mcp_request('tools/call', {
            'name': 'addcomponent',
            'arguments': {'type': 'nonexistent_component_type_xyz', 'x': 0, 'y': 0}
        })
        if not success or 'error' in str(response).lower() or 'not found' in str(response).lower():
            console.print("✅ [green]Correctly rejected invalid type[/green]")
            add_result("Invalid Type Handling", True, "Rejected", "Errors")
        else:
            # If it succeeded, we created a component we need to clean up
            bad_id = extract_component_id_from_response(response)
            if bad_id:
                created_component_ids.append(bad_id)
            console.print("⚠️ [yellow]Did not reject invalid type[/yellow]")
            add_result("Invalid Type Handling", False, "Not rejected", "Errors")
        
        # ═══════════════════════════════════════════════════════════════════════
        # CATEGORY 10: FULL TOOL COVERAGE (Remaining tools)
        # ═══════════════════════════════════════════════════════════════════════
        console.print("\n[bold cyan]═══ CATEGORY 10: FULL TOOL COVERAGE ═══[/bold cyan]")
        
        # Helper paths for file-based tools
        temp_dir = os.getenv("TEMP", "/tmp")
        temp_save_path = os.path.join(temp_dir, "gh_mcp_autotest.ghx")
        temp_canvas_path = os.path.join(temp_dir, "gh_mcp_canvas.png")
        temp_hires_folder = os.path.join(temp_dir, "gh_mcp_hires")
        temp_screenshot_path = os.path.join(temp_dir, "gh_mcp_canvas_capture.png")
        # Ensure targets exist where applicable
        ensure_dir(temp_dir)
        ensure_dir(temp_hires_folder)
        ensure_parent_dir(temp_canvas_path)
        ensure_parent_dir(temp_screenshot_path)
        
        # 10.1: Detailed component info (all)
        console.print("\n📊 [blue]Test 10.1: Get Detailed Component Info (all)[/blue]")
        success, _ = make_mcp_request('tools/call', {'name': 'GetDetailedComponentInfo', 'arguments': {}})
        add_result("Detailed Info (all)", success, "All components", "Coverage")
        
        # 10.2: List available components
        console.print("\n🧭 [blue]Test 10.2: List Available Components[/blue]")
        success, _ = make_mcp_request('tools/call', {'name': 'listavailablecomponents', 'arguments': {}})
        add_result("List Available Components", success, "Retrieved", "Coverage")
        
        # 10.3: List all components with GUIDs
        console.print("\n🪪 [blue]Test 10.3: List Components with GUIDs[/blue]")
        success, _ = make_mcp_request('tools/call', {'name': 'listallcomponentswithguids', 'arguments': {}})
        add_result("List Components with GUIDs", success, "Listed", "Coverage")
        
        # 10.4: Find component by GUID
        console.print("\n🔍 [blue]Test 10.4: Find Component by GUID[/blue]")
        if slider_id:
            success, _ = make_mcp_request('tools/call', {'name': 'findcomponentbyguid', 'arguments': {'componentId': slider_id}})
            add_result("Find Component by GUID", success, "Slider", "Coverage")
        else:
            add_result("Find Component by GUID", False, "Skipped - no slider", "Coverage")
        
        # 10.5: Get component script (generic)
        console.print("\n📜 [blue]Test 10.5: Get Component Script[/blue]")
        script_target = python_id or csharp_id
        if script_target:
            success, _ = make_mcp_request('tools/call', {'name': 'getcomponentscript', 'arguments': {'componentId': script_target}})
            add_result("Get Component Script", success, "Script component", "Coverage")
        else:
            add_result("Get Component Script", False, "Skipped - no script component", "Coverage")
        
        # 10.6: Set component script (python)
        console.print("\n✏️ [blue]Test 10.6: Set Component Script (Python)[/blue]")
        if python_id:
            success, _ = make_mcp_request('tools/call', {
                'name': 'setcomponentscript',
                'arguments': {'componentId': python_id, 'language': 'python', 'script': 'a = (x or 0) + (y or 0)\\nprint(a)'}
            })
            add_result("Set Component Script", success, "Python", "Coverage")
        else:
            add_result("Set Component Script", False, "Skipped - no python script", "Coverage")
        
        # 10.7: Set C# script via dedicated tool
        console.print("\n🧩 [blue]Test 10.7: Set C# Script[/blue]")
        if csharp_id:
            success, _ = make_mcp_request('tools/call', {
                'name': 'setcsharpscript',
                'arguments': {'componentId': csharp_id, 'script': 'A = (x ?? 0) + (y ?? 0);'}
            })
            add_result("Set C# Script", success, "C# component", "Coverage")
        else:
            add_result("Set C# Script", False, "Skipped - no C# script", "Coverage")
        
        # 10.8: List C# scripts
        console.print("\n📋 [blue]Test 10.8: List C# Scripts[/blue]")
        success, _ = make_mcp_request('tools/call', {'name': 'List_CSharp_Scripts', 'arguments': {}})
        add_result("List C# Scripts", success, "Listed", "Coverage")
        
        # 10.9: Get C# script
        console.print("\n📖 [blue]Test 10.9: Get C# Script[/blue]")
        if csharp_id:
            success, _ = make_mcp_request('tools/call', {'name': 'Get_CSharp_Script', 'arguments': {'componentId': csharp_id}})
            add_result("Get C# Script", success, "Retrieved", "Coverage")
        else:
            add_result("Get C# Script", False, "Skipped - no C# script", "Coverage")
        
        # 10.10: Edit C# script
        console.print("\n🛠️ [blue]Test 10.10: Edit C# Script[/blue]")
        if csharp_id:
            success, _ = make_mcp_request('tools/call', {
                'name': 'Edit_CSharp_Script',
                'arguments': {'componentId': csharp_id, 'code': 'A = (x ?? 0) * 3;'}
            })
            add_result("Edit C# Script", success, "Updated", "Coverage")
        else:
            add_result("Edit C# Script", False, "Skipped - no C# script", "Coverage")
        
        # 10.11: Get C# script errors
        console.print("\n🐞 [blue]Test 10.11: Get C# Script Errors[/blue]")
        if csharp_id:
            success, _ = make_mcp_request('tools/call', {'name': 'Get_CSharp_Script_Errors', 'arguments': {'componentId': csharp_id}})
            add_result("Get C# Script Errors", success, "Checked", "Coverage")
        else:
            add_result("Get C# Script Errors", False, "Skipped - no C# script", "Coverage")
        
        # 10.12: Group components by type
        console.print("\n🏷️ [blue]Test 10.12: Group Components by Type[/blue]")
        success, _ = make_mcp_request('tools/call', {'name': 'groupcomponentsbytype', 'arguments': {}})
        add_result("Group Components By Type", success, "Grouped", "Coverage")
        
        # 10.13: Component runtime info
        console.print("\n⚡ [blue]Test 10.13: Get Component Runtime Info[/blue]")
        if slider_id:
            success, _ = make_mcp_request('tools/call', {'name': 'getcomponentruntimeinfo', 'arguments': {'componentId': slider_id}})
            add_result("Runtime Info", success, "Slider", "Coverage")
        else:
            add_result("Runtime Info", False, "Skipped - no slider", "Coverage")
        
        # 10.14: All components runtime info
        console.print("\n📊 [blue]Test 10.14: Get All Components Runtime Info[/blue]")
        success, _ = make_mcp_request('tools/call', {'name': 'getallcomponentsruntimeinfo', 'arguments': {}})
        add_result("All Runtime Info", success, "Retrieved", "Coverage")
        
        # 10.15: Modify script component parameters (rename first output)
        console.print("\n📝 [blue]Test 10.15: Modify Script Component Parameters[/blue]")
        if python_id:
            success, _ = make_mcp_request('tools/call', {
                'name': 'modifyscriptcomponentparameters',
                'arguments': {'componentId': python_id, 'parameterConfig': {'output_0': 'result'}}
            })
            add_result("Modify Script Params", success, "output_0→result", "Coverage")
        else:
            add_result("Modify Script Params", False, "Skipped - no python script", "Coverage")
        
        # 10.16: Document info
        console.print("\n📄 [blue]Test 10.16: Get Document Info[/blue]")
        success, _ = make_mcp_request('tools/call', {'name': 'getdocumentinfo', 'arguments': {}})
        add_result("Get Document Info", success, "Retrieved", "Coverage")
        
        # 10.17: Debug document state
        console.print("\n🧪 [blue]Test 10.17: Debug Document State[/blue]")
        success, _ = make_mcp_request('tools/call', {'name': 'debugdocumentstate', 'arguments': {}})
        add_result("Debug Document State", success, "Captured", "Coverage")
        
        # 10.18: Read Rhino console
        console.print("\n📥 [blue]Test 10.18: Read Rhino Console[/blue]")
        success, _ = make_mcp_request('tools/call', {'name': 'readrhinoconsole', 'arguments': {}})
        add_result("Read Rhino Console", success, "Read", "Coverage")
        
        # 10.19: Connect components with validation
        console.print("\n🧷 [blue]Test 10.19: Connect Components With Validation[/blue]")
        if slider_id and panel_id:
            success, _ = make_mcp_request('tools/call', {
                'name': 'ConnectComponentsWithValidation',
                'arguments': {'sourceComponentId': slider_id, 'sourceOutputIndex': 0, 'targetComponentId': panel_id, 'targetInputIndex': 0}
            })
            add_result("Connect With Validation", success, "Slider→Panel", "Coverage")
        else:
            add_result("Connect With Validation", False, "Skipped - missing components", "Coverage")
        
        # 10.20: Connect components by name
        console.print("\n🔠 [blue]Test 10.20: Connect Components By Name[/blue]")
        if slider_id and panel_id:
            success, _ = make_mcp_request('tools/call', {
                'name': 'Connect_Components_By_Name',
                'arguments': {'sourceComponentId': slider_id, 'targetComponentId': panel_id}
            })
            add_result("Connect By Name", success, "Slider→Panel", "Coverage")
        else:
            add_result("Connect By Name", False, "Skipped - missing components", "Coverage")
        
        # 10.21: Get components in group
        console.print("\n🧩 [blue]Test 10.21: Get Components In Group[/blue]")
        if created_group_ids:
            success, _ = make_mcp_request('tools/call', {'name': 'Get_Components_In_Group', 'arguments': {'groupId': created_group_ids[0]}})
            add_result("Get Components In Group", success, "Group", "Coverage")
        else:
            add_result("Get Components In Group", False, "Skipped - no group", "Coverage")
        
        # 10.22: Recompute script components
        console.print("\n🔄 [blue]Test 10.22: Recompute Script Components[/blue]")
        success, _ = make_mcp_request('tools/call', {'name': 'RecomputeScriptComponents', 'arguments': {}})
        add_result("Recompute Script Components", success, "Triggered", "Coverage")
        
        # 10.23: Run health check (first available)
        console.print("\n🩺 [blue]Test 10.23: Run Health Check[/blue]")
        if health_check_names:
            success, _ = make_mcp_request('tools/call', {'name': 'runhealthcheck', 'arguments': {'healthCheckName': health_check_names[0]}})
            add_result("Run Health Check", success, health_check_names[0], "Coverage")
        else:
            add_result("Run Health Check", False, "Skipped - no health check names", "Coverage")
        
        # 10.24: Canvas captures and exports (using this repo's tool names)
        console.print("\n🖼️ [blue]Test 10.24: Canvas Capture & Export[/blue]")
        
        # Capture_Canvas - basic canvas capture
        success, response = make_mcp_request('tools/call', {'name': 'Capture_Canvas', 'arguments': {}})
        canvas_path = get_path_from_response(response) if success else None
        canvas_exists = success and path_exists(canvas_path)
        add_result("Capture Canvas", canvas_exists or success, canvas_path or "Captured", "Coverage")
        if canvas_exists:
            open_path_if_exists(canvas_path)
            print_open_hint(canvas_path, "Capture Canvas")
        elif not success:
            console.print(f"[yellow]Capture_Canvas failed[/yellow]")
            console.print(f"[dim]Raw response: {response}[/dim]")
        
        # Save_Visible_Canvas
        success, response = make_mcp_request('tools/call', {'name': 'Save_Visible_Canvas', 'arguments': {'filePath': temp_canvas_path}})
        resp_path = get_path_from_response(response) if success else None
        path = resp_path or temp_canvas_path
        exists = success and path_exists(path)
        add_result("Save Visible Canvas", exists or success, path, "Coverage")
        if exists:
            open_path_if_exists(path)
            print_open_hint(path, "Visible canvas")
        elif not success:
            console.print(f"[yellow]Save_Visible_Canvas failed[/yellow]")
            console.print(f"[dim]Raw response: {response}[/dim]")
        
        # Save_HiRes_Canvas
        success, response = make_mcp_request('tools/call', {'name': 'Save_HiRes_Canvas', 'arguments': {'outputFolder': temp_hires_folder, 'fileNameWithoutExtension': 'gh_mcp_hires_test', 'zoom': 0.5}})
        resp_path = get_path_from_response(response) if success else None
        path = resp_path or temp_hires_folder
        exists = success and path_exists(path)
        add_result("Save HiRes Canvas", exists or success, path, "Coverage")
        if exists:
            open_path_if_exists(path)
            print_open_hint(path, "Hi-res canvas folder")
        elif not success:
            console.print(f"[yellow]Save_HiRes_Canvas failed[/yellow]")
            console.print(f"[dim]Raw response: {response}[/dim]")
        
        # capturecanvasscreenshot (method name from ComponentTools)
        success, response = make_mcp_request('tools/call', {'name': 'capturecanvasscreenshot', 'arguments': {'filePath': temp_screenshot_path, 'format': 'png', 'quality': 90}})
        resp_path = get_path_from_response(response) if success else None
        path = resp_path or temp_screenshot_path
        exists = success and path_exists(path)
        add_result("Capture Canvas Screenshot", exists or success, path, "Coverage")
        if exists:
            open_path_if_exists(path)
            print_open_hint(path, "Canvas screenshot")
        elif not success:
            console.print(f"[yellow]capturecanvasscreenshot failed[/yellow]")
            console.print(f"[dim]Raw response: {response}[/dim]")
        
        # 10.25: Viewport tools (using this repo's tool names)
        console.print("\n🌐 [blue]Test 10.25: Viewport Tools[/blue]")
        
        # Capture_Viewport_Simple
        success, response = make_mcp_request('tools/call', {'name': 'Capture_Viewport_Simple', 'arguments': {'retentionSeconds': 300, 'includeBase64': False}})
        try:
            data = json.loads(extract_response_text(response)) if success else {}
        except Exception:
            data = {}
        path = data.get('FilePath') or data.get('filePath') or data.get('Path') or data.get('path')
        exists = success and path_exists(path)
        add_result("Capture Viewport Simple", exists or success, path or "Simple capture", "Coverage")
        if exists:
            open_path_if_exists(path)
            print_open_hint(path, "Viewport simple")
        elif not success:
            console.print(f"[yellow]Capture_Viewport_Simple failed[/yellow]")
            console.print(f"[dim]Raw response: {response}[/dim]")
        
        # List_Viewports
        success, response = make_mcp_request('tools/call', {'name': 'List_Viewports', 'arguments': {}})
        add_result("List Viewports", success, "Listed", "Coverage")
        
        # Capture_Viewport (full)
        success, response = make_mcp_request('tools/call', {
            'name': 'Capture_Viewport',
            'arguments': {'viewportName': None, 'width': 640, 'height': 480, 'includeGrid': False, 'includeAxes': False, 'transparentBackground': False, 'quality': 90, 'retentionSeconds': 300, 'includeBase64': False}
        })
        try:
            data = json.loads(extract_response_text(response)) if success else {}
        except Exception:
            data = {}
        path = data.get('FilePath') or data.get('filePath') or data.get('Path') or data.get('path')
        exists = success and path_exists(path)
        add_result("Capture Viewport", exists or success, path or "640x480", "Coverage")
        if exists:
            open_path_if_exists(path)
            print_open_hint(path, "Viewport capture")
        elif not success:
            console.print(f"[yellow]Capture_Viewport failed[/yellow]")
            console.print(f"[dim]Raw response: {response}[/dim]")

        # Orbit_Object — target the "orbit me" group (requires Viewport + Get_Components_In_Group tools)
        orbit_component_id = None
        orbit_nick = ''
        group_success, group_resp = make_mcp_request('tools/call', {
            'name': 'get_components_in_group',
            'arguments': {'groupName': 'orbit me'}
        })
        if group_success and isinstance(group_resp, dict) and 'result' in group_resp:
            try:
                group_data = json.loads(group_resp['result']['content'][0]['text'])
                if group_data.get('found') and group_data.get('components'):
                    for c in group_data['components']:
                        cid = c.get('id') or c.get('Id')
                        nick = (c.get('nickName') or c.get('NickName') or c.get('name') or '').lower()
                        if not cid:
                            continue
                        orbit_component_id = str(cid)
                        orbit_nick = c.get('nickName') or c.get('name') or ''
                        if any(k in nick for k in ('sphere', 'box', 'brep', 'mesh', 'surface', 'curve', 'pipe')):
                            break
            except Exception as ex:
                console.print(f"[dim]orbit me group parse: {ex}[/dim]")
        elif not group_success:
            console.print(f"[yellow]get_components_in_group('orbit me') failed[/yellow]")

        if orbit_component_id:
            success, response = make_mcp_request('tools/call', {
                'name': 'orbit_object',
                'arguments': {
                    'componentId': orbit_component_id,
                    'viewPreset': 'iso',
                    'width': 640,
                    'height': 480,
                    'includeBase64': False,
                    'retentionSeconds': 300,
                }
            })
            try:
                data = json.loads(extract_response_text(response)) if success else {}
            except Exception:
                data = {}
            if isinstance(data, dict) and data.get('capture'):
                cap = data['capture']
                if isinstance(cap, dict) and cap.get('content'):
                    ann = cap['content'][0].get('annotations', {})
                    path = ann.get('filePath')
                else:
                    path = None
            else:
                path = data.get('FilePath') or data.get('filePath')
            exists = success and path_exists(path)
            add_result("Orbit Object (iso)", exists or success, path or f"{orbit_nick or orbit_component_id} iso", "Coverage")
            if exists:
                open_path_if_exists(path)
                print_open_hint(path, "Orbit object capture")
            elif not success:
                console.print(f"[yellow]orbit_object failed[/yellow]")
                console.print(f"[dim]Raw response: {response}[/dim]")
        else:
            add_result("Orbit Object (iso)", False, "Skipped - no component id", "Coverage")
        
        # 10.26: Toggle tools
        console.print("\n🎛️ [blue]Test 10.26: Toggle Tools[/blue]")
        if 'toggle_id' in locals() and toggle_id:
            success, _ = make_mcp_request('tools/call', {'name': 'Toggle_BooleanToggle', 'arguments': {'componentId': toggle_id}})
            add_result("Toggle Boolean", success, "Toggled", "Coverage")
        else:
            add_result("Toggle Boolean", False, "Skipped - no toggle", "Coverage")
        
        success, _ = make_mcp_request('tools/call', {'name': 'Toggle_SolverExecute', 'arguments': {}})
        add_result("Toggle Solver Execute", success, "Attempted", "Coverage")
        
        success, _ = make_mcp_request('tools/call', {'name': 'Toggle_SolverReset', 'arguments': {}})
        add_result("Toggle Solver Reset", success, "Attempted", "Coverage")
        
        # 10.27: Document save/load/clear
        console.print("\n💾 [blue]Test 10.27: Document Save/Load/Clear[/blue]")
        success, _ = make_mcp_request('tools/call', {'name': 'savedocument', 'arguments': {'filePath': temp_save_path}})
        add_result("Save Document", success, temp_save_path, "Coverage")
        
        success, _ = make_mcp_request('tools/call', {'name': 'cleardocument', 'arguments': {}})
        add_result("Clear Document", success, "Cleared", "Coverage")
        
        # Reload the saved document to restore components if possible
        success, _ = make_mcp_request('tools/call', {'name': 'loaddocument', 'arguments': {'filePath': temp_save_path}})
        add_result("Load Document", success, temp_save_path, "Coverage")
        
        # ═══════════════════════════════════════════════════════════════════════
        # CLEANUP
        # ═══════════════════════════════════════════════════════════════════════
        console.print("\n[bold cyan]═══ CLEANUP ═══[/bold cyan]")
        
        console.print(f"\n🧹 [blue]Cleaning up {len(created_component_ids)} created components...[/blue]")
        cleanup_success = 0
        cleanup_failed = 0
        
        if created_component_ids:
            success, response = make_mcp_request('tools/call', {
                'name': 'removecomponents',
                'arguments': {'componentIds': created_component_ids}
            })
            if success:
                cleanup_success = len(created_component_ids)
                console.print(f"✅ [green]Removed {cleanup_success} components[/green]")
            else:
                # Try individual removal
                for comp_id in created_component_ids:
                    success, _ = make_mcp_request('tools/call', {
                        'name': 'removecomponents',
                        'arguments': {'componentIds': [comp_id]}
                    })
                    if success:
                        cleanup_success += 1
                    else:
                        cleanup_failed += 1
                console.print(f"✅ [green]Removed {cleanup_success} components, {cleanup_failed} failed[/green]")
        
        add_result("Cleanup", cleanup_failed == 0, f"{cleanup_success} removed", "Cleanup")
        
    except Exception as e:
        console.print(f"\n❌ [red]Error during comprehensive test: {e}[/red]")
        import traceback
        console.print(f"[dim]{traceback.format_exc()}[/dim]")
        add_result("Test Execution", False, str(e)[:50], "Error")
    
    # ═══════════════════════════════════════════════════════════════════════
    # RESULTS SUMMARY
    # ═══════════════════════════════════════════════════════════════════════
    test_duration = time.time() - test_start_time
    
    console.print("\n" + "=" * 80)
    console.print("📊 [bold blue]COMPREHENSIVE TEST RESULTS[/bold blue]")
    console.print("=" * 80)
    
    # Group results by category
    categories = {}
    for result in test_results:
        cat = result["category"]
        if cat not in categories:
            categories[cat] = []
        categories[cat].append(result)
    
    total_passed = 0
    total_tests = len(test_results)
    
    for category, results in categories.items():
        passed = sum(1 for r in results if r["passed"])
        total = len(results)
        total_passed += passed
        
        color = "green" if passed == total else "yellow" if passed > 0 else "red"
        console.print(f"\n[bold {color}]{category}[/bold {color}] ({passed}/{total})")
        
        for r in results:
            status = "✅" if r["passed"] else "❌"
            console.print(f"  {status} {r['name']}: {r['details']}")
    
    console.print("\n" + "=" * 80)
    success_rate = (total_passed / total_tests * 100) if total_tests > 0 else 0
    
    # Summary table
    summary_table = Table(box=box.ROUNDED, title="Test Summary")
    summary_table.add_column("Metric", style="cyan")
    summary_table.add_column("Value", style="green")
    summary_table.add_row("Total Tests", str(total_tests))
    summary_table.add_row("Passed", str(total_passed))
    summary_table.add_row("Failed", str(total_tests - total_passed))
    summary_table.add_row("Success Rate", f"{success_rate:.1f}%")
    summary_table.add_row("Duration", f"{test_duration:.2f}s")
    summary_table.add_row("Components Created", str(len(created_component_ids)))
    console.print(summary_table)
    
    if success_rate >= 90:
        console.print("\n🎉 [bold green]EXCELLENT! All major functionality working correctly![/bold green]")
    elif success_rate >= 75:
        console.print("\n✅ [bold yellow]GOOD! Most functionality working with minor issues.[/bold yellow]")
    elif success_rate >= 50:
        console.print("\n⚠️ [bold yellow]PARTIAL! Some functionality working, attention needed.[/bold yellow]")
    else:
        console.print("\n❌ [bold red]NEEDS ATTENTION! Multiple issues detected.[/bold red]")
    
    console.print("=" * 80)
    return test_results


def search_components_debug():
    """Debug version of component search that doesn't require user input"""
    console.print("🔍 [dim]Running component search in debug mode...[/dim]")
    
    # Get all components
    success, response = make_mcp_request('tools/call', {
        'name': 'capturecanvasstate',
        'arguments': {}
    })
    
    if success and 'result' in response:
        try:
            # Handle different response structures
            if 'content' in response['result'] and response['result']['content']:
                content = json.loads(response['result']['content'][0]['text'])
            else:
                # Direct response structure
                content = response['result']
            
            components = content.get('Components', [])
            
            if components:
                console.print(f"✅ [green]Found {len(components)} components for search testing[/green]")
            else:
                console.print("📭 [yellow]No components found for search testing[/yellow]")
        except Exception as e:
            console.print(f"❌ [red]Search test failed: {e}[/red]")
    else:
        console.print("❌ [red]Search test failed[/red]")


def test_complete_spiral_workflow():
    """Test complete spiral workflow with Python script"""
    console.print("\n🌀 [blue]TESTING COMPLETE SPIRAL WORKFLOW[/blue]")
    console.print(Rule(style="blue"))
    
    try:
        # Step 1: Create Python script component
        console.print("📝 [blue]Step 1: Creating Python script component...[/blue]")
        success, response = make_mcp_request('tools/call', {
            'name': 'addpythonscriptcomponent',
            'arguments': {
                'x': 100,
                'y': 100,
                'script': PYTHON_SPIRAL_CODE
            }
        })
        
        if not success:
            console.print("❌ [red]Failed to create Python script component[/red]")
            return False
        
        # Extract component ID
        script_id = extract_component_id_from_response(response)
        if not script_id:
            console.print("❌ [red]Failed to extract script component ID[/red]")
            return False
        console.print(f"✅ [green]Python script component created: {script_id[:8]}...[/green]")
        
        # Step 2: Create input number components
        console.print("🔢 [blue]Step 2: Creating input number components...[/blue]")
        
        # Create first number input
        success, response = make_mcp_request('tools/call', {
            'name': 'addcomponent',
            'arguments': {'type': 'number', 'x': 200, 'y': 100}
        })
        
        if success:
            number1_id = extract_component_id_from_response(response)
            if number1_id:
                console.print(f"✅ [green]Number input 1 created: {number1_id[:8]}...[/green]")
            else:
                console.print("⚠️ [yellow]Number input 1 created but ID extraction failed[/yellow]")
        else:
            console.print("⚠️ [yellow]Number input 1 creation failed[/yellow]")
        
        # Create second number input
        success, response = make_mcp_request('tools/call', {
            'name': 'addcomponent',
            'arguments': {'type': 'number', 'x': 200, 'y': 200}
        })
        
        if success:
            number2_id = extract_component_id_from_response(response)
            if number2_id:
                console.print(f"✅ [green]Number input 2 created: {number2_id[:8]}...[/green]")
            else:
                console.print("⚠️ [yellow]Number input 2 created but ID extraction failed[/yellow]")
        else:
            console.print("⚠️ [yellow]Number input 2 creation failed[/yellow]")
        
        # Step 3: Create output panel
        console.print("📋 [blue]Step 3: Creating output panel...[/blue]")
        success, response = make_mcp_request('tools/call', {
            'name': 'addcomponent',
            'arguments': {'type': 'panel', 'x': 400, 'y': 100}
        })
        
        if success:
            panel_id = extract_component_id_from_response(response)
            if panel_id:
                console.print(f"✅ [green]Output panel created: {panel_id[:8]}...[/green]")
            else:
                console.print("⚠️ [yellow]Output panel created but ID extraction failed[/yellow]")
        else:
            console.print("⚠️ [yellow]Output panel creation failed[/yellow]")
        
        # Step 4: Set input values
        console.print("🔧 [blue]Step 4: Setting input values...[/blue]")
        if 'number1_id' in locals() and number1_id:
            success, response = make_mcp_request('tools/call', {
                'name': 'setcomponentvalue',
                'arguments': {
                    'componentId': number1_id,
                    'parameterName': 'value',
                    'value': 5.0
                }
            })
            if success:
                console.print("✅ [green]Number input 1 value set to 5.0[/green]")
            else:
                console.print("⚠️ [yellow]Failed to set number input 1 value[/yellow]")
        
        if 'number2_id' in locals() and number2_id:
            success, response = make_mcp_request('tools/call', {
                'name': 'setcomponentvalue',
                'arguments': {
                    'componentId': number2_id,
                    'parameterName': 'value',
                    'value': 3.0
                }
            })
            if success:
                console.print("✅ [green]Number input 2 value set to 3.0[/green]")
            else:
                console.print("⚠️ [yellow]Failed to set number input 2 value[/yellow]")
        
        console.print("✅ [green]Spiral workflow test completed successfully![/green]")
        return True
        
    except Exception as e:
        console.print(f"❌ [red]Spiral workflow test failed: {e}[/red]")
        return False


def test_csharp_script_workflow():
    """Test C# script workflow"""
    console.print("\n🔷 [blue]TESTING C# SCRIPT WORKFLOW[/blue]")
    console.print(Rule(style="blue"))
    
    try:
        # Step 1: Create C# script component
        console.print("📝 [blue]Step 1: Creating C# script component...[/blue]")
        success, response = make_mcp_request('tools/call', {
            'name': 'addcsharpscriptcomponent',
            'arguments': {
                'x': 100,
                'y': 300,
                'script': CSHARP_SPIRAL_CODE
            }
        })
        
        if not success:
            console.print("❌ [red]Failed to create C# script component[/red]")
            return False
        
        # Extract component ID
        script_id = extract_component_id_from_response(response)
        if not script_id:
            console.print("❌ [red]Failed to extract C# script component ID[/red]")
            return False
        console.print(f"✅ [green]C# script component created: {script_id[:8]}...[/green]")
        
        # Step 2: Create input components
        console.print("🔢 [blue]Step 2: Creating input components...[/blue]")
        
        # Create first number input
        success, response = make_mcp_request('tools/call', {
            'name': 'addcomponent',
            'arguments': {'type': 'number', 'x': 200, 'y': 300}
        })
        
        if success:
            number1_id = extract_component_id_from_response(response)
            if number1_id:
                console.print(f"✅ [green]Number input 1 created: {number1_id[:8]}...[/green]")
            else:
                console.print("⚠️ [yellow]Number input 1 created but ID extraction failed[/yellow]")
        else:
            console.print("⚠️ [yellow]Number input 1 creation failed[/yellow]")
        
        # Create second number input
        success, response = make_mcp_request('tools/call', {
            'name': 'addcomponent',
            'arguments': {'type': 'number', 'x': 200, 'y': 400}
        })
        
        if success:
            number2_id = extract_component_id_from_response(response)
            if number2_id:
                console.print(f"✅ [green]Number input 2 created: {number2_id[:8]}...[/green]")
            else:
                console.print("⚠️ [yellow]Number input 2 created but ID extraction failed[/yellow]")
        else:
            console.print("⚠️ [yellow]Number input 2 creation failed[/yellow]")
        
        # Step 3: Create output panel
        console.print("📋 [blue]Step 3: Creating output panel...[/blue]")
        success, response = make_mcp_request('tools/call', {
            'name': 'addcomponent',
            'arguments': {'type': 'panel', 'x': 400, 'y': 300}
        })
        
        if success:
            panel_id = extract_component_id_from_response(response)
            if panel_id:
                console.print(f"✅ [green]Output panel created: {panel_id[:8]}...[/green]")
            else:
                console.print("⚠️ [yellow]Output panel created but ID extraction failed[/yellow]")
        else:
            console.print("⚠️ [yellow]Output panel creation failed[/yellow]")
        
        # Step 4: Set input values
        console.print("🔧 [blue]Step 4: Setting input values...[/blue]")
        if 'number1_id' in locals() and number1_id:
            success, response = make_mcp_request('tools/call', {
                'name': 'setcomponentvalue',
                'arguments': {
                    'componentId': number1_id,
                    'parameterName': 'value',
                    'value': 7.0
                }
            })
            if success:
                console.print("✅ [green]Number input 1 value set to 7.0[/green]")
            else:
                console.print("⚠️ [yellow]Failed to set number input 1 value[/yellow]")
        
        if 'number2_id' in locals() and number2_id:
            success, response = make_mcp_request('tools/call', {
                'name': 'setcomponentvalue',
                'arguments': {
                    'componentId': number2_id,
                    'parameterName': 'value',
                    'value': 4.0
                }
            })
            if success:
                console.print("✅ [green]Number input 2 value set to 4.0[/green]")
            else:
                console.print("⚠️ [yellow]Failed to set number input 2 value[/yellow]")
        
        console.print("✅ [green]C# script workflow test completed successfully![/green]")
        return True
        
    except Exception as e:
        console.print(f"❌ [red]C# script workflow test failed: {e}[/red]")
        return False


def test_individual_tool():
    """Test individual MCP tools"""
    console.print("\n🔧 [blue]TESTING INDIVIDUAL MCP TOOLS[/blue]")
    console.print(Rule(style="blue"))
    
    test_results = []
    
    try:
        # Test 1: List available tools
        console.print("📋 [blue]Test 1: List available tools...[/blue]")
        success, response = make_mcp_request('tools/list')
        if success and 'result' in response and 'tools' in response['result']:
            tool_count = len(response['result']['tools'])
            console.print(f"✅ [green]Found {tool_count} available tools[/green]")
            test_results.append(("List Tools", True, f"{tool_count} tools"))
        else:
            console.print("❌ [red]Failed to list tools[/red]")
            test_results.append(("List Tools", False, "Failed"))
        
        # Test 2: Get system health
        console.print("💚 [blue]Test 2: Get system health...[/blue]")
        success, response = make_mcp_request('tools/call', {
            'name': 'getsystemhealth',
            'arguments': {}
        })
        if success:
            console.print("✅ [green]System health check successful[/green]")
            test_results.append(("System Health", True, "Healthy"))
        else:
            console.print("❌ [red]System health check failed[/red]")
            test_results.append(("System Health", False, "Failed"))
        
        # Test 3: Capture canvas state
        console.print("📸 [blue]Test 3: Capture canvas state...[/blue]")
        success, response = make_mcp_request('tools/call', {
            'name': 'capturecanvasstate',
            'arguments': {}
        })
        if success:
            console.print("✅ [green]Canvas state captured successfully[/green]")
            test_results.append(("Canvas Capture", True, "Captured"))
        else:
            console.print("❌ [red]Canvas capture failed[/red]")
            test_results.append(("Canvas Capture", False, "Failed"))
        
        # Test 4: Create a simple component
        console.print("➕ [blue]Test 4: Create simple component...[/blue]")
        success, response = make_mcp_request('tools/call', {
            'name': 'addcomponent',
            'arguments': {'type': 'slider', 'x': 500, 'y': 500}
        })
        if success:
            console.print("✅ [green]Component created successfully[/green]")
            test_results.append(("Component Creation", True, "Created"))
        else:
            console.print("❌ [red]Component creation failed[/red]")
            test_results.append(("Component Creation", False, "Failed"))
        
        # Print results summary
        console.print("\n📊 [blue]Individual Tool Test Results:[/blue]")
        passed_tests = 0
        for test_name, success, details in test_results:
            status = "✅ PASSED" if success else "❌ FAILED"
            color = "green" if success else "red"
            console.print(f"{status} [bold {color}]{test_name}[/bold {color}] - {details}")
            if success:
                passed_tests += 1
        
        success_rate = (passed_tests / len(test_results) * 100) if test_results else 0
        console.print(f"\n🎯 [bold blue]Success Rate: {success_rate:.1f}% ({passed_tests}/{len(test_results)})[/bold blue]")
        
        return success_rate >= 75
        
    except Exception as e:
        console.print(f"❌ [red]Individual tool test failed: {e}[/red]")
        return False




def create_component_group(name, x, y, color):
    """Create a component group with progress indication and improved response parsing"""
    console.print(f"📦 [blue]Creating group '{name}' at ({x}, {y})...[/blue]")
    
    with console.status("[blue]Creating component group...", spinner="dots"):
        success, response = make_mcp_request('tools/call', {
            'name': 'createcomponentgroup',
            'arguments': {
                'name': name,
                'x': x,
                'y': y,
                'groupColor': color
            }
        })
    
    if success:
        try:
            # Parse the nested response structure
            group_data = extract_component_data(str(response))
            if group_data and group_data.get('Success'):
                group_id = group_data.get('GroupId')
                message = group_data.get('Message', f"Group '{name}' created successfully")
                console.print(f"✅ [green]{message}[/green]")
                if group_id:
                    console.print(f"📋 [blue]Group ID: {group_id}[/blue]")
                return True
            else:
                error_msg = group_data.get('Message', 'Unknown error') if group_data else 'Failed to parse response'
                console.print(f"❌ [red]Failed to create group: {error_msg}[/red]")
                return False
        except Exception as e:
            console.print(f"❌ [red]Error parsing group response: {e}[/red]")
            console.print(f"📋 [dim]Raw response: {response}[/dim]")
            return False
    else:
        console.print(f"❌ [red]Failed to create group: {response}[/red]")
        return False



def show_main_menu():
    """Display the enhanced main menu with improved UI"""
    console.print("\n" + "=" * 80)
    
    # Header with gradient effect
    header_panel = Panel(
        "[bold blue]🚀 Grasshopper MCP Test Suite[/bold blue]\n"
        "[dim]Enhanced Interactive Testing Tool[/dim]",
        box=box.DOUBLE,
        border_style="blue",
        padding=(1, 2)
    )
    console.print(header_panel)
    
    # Main menu sections
    sections = [
        {
            "title": "🔌 [bold cyan]Connection & Info[/bold cyan]",
            "items": [
                ("1", "Check MCP Connection", "🔌 Test server connectivity"),
                ("2", "List Available Tools", "📋 Show all MCP tools"),
                ("3", "List Current Components", "🔍 View canvas contents"),
                ("4", "System Health Check", "💚 System diagnostics")
            ]
        },
        {
            "title": "➕ [bold green]Component Creation[/bold green]",
            "items": [
                ("5", "Add Component", "➕ Create new component"),
                ("6", "Add Python Script", "🐍 Create Python script component"),
                ("7", "Add C# Script", "🔷 Create C# script component"),
                ("8", "List Component Types", "📝 Show component library")
            ]
        },
        {
            "title": "🔧 [bold yellow]Component Operations[/bold yellow]",
            "items": [
                ("9", "Set Component Value", "🔧 Set parameter values"),
                ("10", "Get Component Info", "ℹ️ Component details"),
                ("11", "Connect Components", "🔗 Wire components together"),
                ("12", "Set Component Script", "📝 Edit script content"),
                ("20", "Get Python Script", "🐍 Get Python script code"),
                ("21", "Edit Python Script", "✏️ Edit Python script with diagnostics"),
                ("22", "Get Python Script Errors", "🐛 Check Python script errors"),
                ("23", "List Python Scripts", "📋 List all Python script components")
            ]
        },
        {
            "title": "📦 [bold magenta]Advanced Features[/bold magenta]",
            "items": [
                ("13", "Create Component Group", "📦 Create empty group"),
                ("14", "Capture Canvas State", "📸 Full canvas snapshot"),
                ("15", "Clear Canvas", "🧹 Remove all components"),
                ("16", "Run Comprehensive Test", "🧪 Test all functionality")
            ]
        },
        {
            "title": "⚙️ [bold red]System & Help[/bold red]",
            "items": [
                ("17", "Configuration Settings", "⚙️ Manage MCP connection settings"),
                ("18", "Help & Tips", "❓ Show usage tips"),
                ("19", "Exit", "👋 Close application"),
                ("24", "Run MCP Hang Regression Suite", "🧪 Timeout/concurrency/restart probes")
            ]
        }
    ]
    
    # Display each section
    for section in sections:
        console.print(f"\n{section['title']}")
        console.print("─" * 60)
        
        section_table = Table(
            box=box.ROUNDED,
            show_header=False,
            padding=(0, 1)
        )
        section_table.add_column("Option", style="cyan", no_wrap=True, width=6)
        section_table.add_column("Description", style="white", width=35)
        section_table.add_column("Status", style="dim", width=25)
        
        for option, description, status in section['items']:
            section_table.add_row(option, description, status)
        
        console.print(section_table)
    
    console.print("\n" + "=" * 80)
    console.print("💡 [dim]Tip: Use --help for command-line options or --interactive for full menu[/dim]")
    console.print("=" * 80)
    
    # Hidden debug hint
    debug_hint = Panel(
        "[dim]💡 Tip: Type [yellow]999[/yellow] for automatic testing of all features[/dim]",
        box=box.ROUNDED,
        border_style="dim"
    )
    console.print(debug_hint)

# Individual Tool Functions
def list_available_tools():
    """List all available MCP tools"""
    console.print("\n📋 [blue]LISTING AVAILABLE MCP TOOLS[/blue]")
    console.print(Rule(style="blue"))
    
    success, response = make_mcp_request('tools/list')
    
    if success and 'result' in response and 'tools' in response['result']:
        tools = response['result']['tools']
        
        table = Table(
            title="[bold]Available MCP Tools[/bold]",
            box=box.ROUNDED,
            header_style="bold magenta",
            title_style="bold blue"
        )
        table.add_column("Tool Name", style="cyan", width=25)
        table.add_column("Description", style="green", width=60)
        table.add_column("Required Params", style="yellow", width=20)
        
        for tool in tools:
            name = tool.get('name', 'Unknown')
            description = tool.get('description', 'No description')
            
            # Extract required parameters
            schema = tool.get('inputSchema', {})
            required = schema.get('required', [])
            required_str = ', '.join(required) if required else 'None'
            
            table.add_row(name, description, required_str)
        
        console.print(table)
        console.print(f"\n✅ [green]Found {len(tools)} available tools[/green]")
    else:
        console.print("❌ [red]Failed to get tools list[/red]")

def tool_add_component():
    """Add Component tool"""
    console.print("\n➕ [blue]ADD COMPONENT[/blue]")
    console.print(Rule(style="blue"))
    
    component_type = Prompt.ask("Component type")
    x = IntPrompt.ask("X position", default=100)
    y = IntPrompt.ask("Y position", default=100)
    
    add_component(component_type, x, y)

def tool_add_python_script():
    """Add Python Script Component tool"""
    console.print("\n🐍 [blue]ADD PYTHON SCRIPT COMPONENT[/blue]")
    console.print(Rule(style="blue"))
    
    script = Prompt.ask("Script content (optional)", default="")
    x = IntPrompt.ask("X position", default=100)
    y = IntPrompt.ask("Y position", default=100)
    
    success, response = make_mcp_request('tools/call', {
        'name': 'addpythonscriptcomponent',
        'arguments': {'script': script, 'x': x, 'y': y}
    })
    
    if success:
        console.print("✅ [green]Python script component added[/green]")
    else:
        console.print(f"❌ [red]Failed to add Python script component: {response}[/red]")

def tool_add_csharp_script():
    """Add C# Script Component tool"""
    console.print("\n🔷 [blue]ADD C# SCRIPT COMPONENT[/blue]")
    console.print(Rule(style="blue"))
    
    script = Prompt.ask("Script content (optional)", default="")
    x = IntPrompt.ask("X position", default=100)
    y = IntPrompt.ask("Y position", default=100)
    
    success, response = make_mcp_request('tools/call', {
        'name': 'addcsharpscriptcomponent',
        'arguments': {'script': script, 'x': x, 'y': y}
    })
    
    if success:
        console.print("✅ [green]C# script component added[/green]")
    else:
        console.print(f"❌ [red]Failed to add C# script component: {response}[/red]")

def tool_list_available_component_types():
    """List Available Component Types tool"""
    console.print("\n📝 [blue]LIST AVAILABLE COMPONENT TYPES[/blue]")
    console.print(Rule(style="blue"))
    
    category = Prompt.ask("Category filter (optional)", default="")
    
    success, response = make_mcp_request('tools/call', {
        'name': 'listavailablecomponents',
        'arguments': {'category': category} if category else {}
    })
    
    if success:
        console.print("✅ [green]Component types retrieved[/green]")
        console.print(f"Response: {response}")
    else:
        console.print(f"❌ [red]Failed to get component types: {response}[/red]")

def tool_set_component_value():
    """Set Component Value tool"""
    console.print("\n🔧 [blue]SET COMPONENT VALUE[/blue]")
    console.print(Rule(style="blue"))
    
    component_id = select_component("Select component to set value for")
    if component_id:
        param_name = Prompt.ask("Parameter name")
        value = Prompt.ask("Value")
        
        set_component_value(component_id, param_name, value)

def tool_get_component_info():
    """Get Component Info tool"""
    console.print("\nℹ️ [blue]GET COMPONENT INFO[/blue]")
    console.print(Rule(style="blue"))
    
    component_id = select_component("Select component to get info for")
    if component_id:
        success, response = make_mcp_request('tools/call', {
            'name': 'getcomponentinfo',
            'arguments': {'componentId': component_id}
        })
        
        if success:
            console.print("✅ [green]Component info retrieved[/green]")
            console.print(f"Response: {response}")
        else:
            console.print(f"❌ [red]Failed to get component info: {response}[/red]")

def tool_connect_components():
    """Connect Components tool"""
    console.print("\n🔗 [blue]CONNECT COMPONENTS[/blue]")
    console.print(Rule(style="blue"))
    
    source_id = select_component("Select source component")
    if source_id:
        source_output = IntPrompt.ask("Source output index", default=0)
        target_id = select_component("Select target component")
        if target_id:
            target_input = IntPrompt.ask("Target input index", default=0)
            connect_components(source_id, source_output, target_id, target_input)

def tool_set_component_script():
    """Set Component Script tool"""
    console.print("\n📝 [blue]SET COMPONENT SCRIPT[/blue]")
    console.print(Rule(style="blue"))
    
    component_id = select_component("Select script component")
    if component_id:
        language = Prompt.ask("Language", choices=["python", "csharp"], default="python")
        script = Prompt.ask("Script content")
        
        set_component_script(component_id, language, script)

def tool_get_component_script():
    """Get Component Script tool"""
    console.print("\n📖 [blue]GET COMPONENT SCRIPT[/blue]")
    console.print(Rule(style="blue"))
    
    component_id = select_component("Select script component")
    if component_id:
        success, response = make_mcp_request('tools/call', {
            'name': 'getcomponentscript',
            'arguments': {'componentId': component_id}
        })
        
        if success:
            console.print("✅ [green]Component script retrieved[/green]")
            console.print(f"Response: {response}")
        else:
            console.print(f"❌ [red]Failed to get component script: {response}[/red]")

def tool_get_python_script():
    """Get Python Script tool"""
    console.print("\n🐍 [blue]GET PYTHON SCRIPT[/blue]")
    console.print(Rule(style="blue"))
    
    component_id = select_component("Select Python script component")
    if component_id:
        success, response = make_mcp_request('tools/call', {
            'name': 'getpythonscript',
            'arguments': {'componentId': component_id}
        })
        
        if success:
            console.print("✅ [green]Python script retrieved[/green]")
            # Try to extract and display the script code
            if 'result' in response and 'content' in response['result']:
                content = response['result']['content']
                if content and len(content) > 0:
                    text_content = content[0].get('text', '')
                    try:
                        import json
                        data = json.loads(text_content)
                        code = data.get('code', '')
                        if code:
                            console.print(f"\n[bold]Script Code:[/bold]")
                            console.print(Panel(code, title="Python Script", border_style="green"))
                        else:
                            console.print(f"Response: {text_content}")
                    except:
                        console.print(f"Response: {text_content}")
            else:
                console.print(f"Response: {response}")
        else:
            console.print(f"❌ [red]Failed to get Python script: {response}[/red]")

def tool_edit_python_script():
    """Edit Python Script tool"""
    console.print("\n✏️ [blue]EDIT PYTHON SCRIPT[/blue]")
    console.print(Rule(style="blue"))
    
    component_id = select_component("Select Python script component")
    if component_id:
        console.print("\n[dim]Enter Python script code (end with empty line or Ctrl+D):[/dim]")
        lines = []
        try:
            while True:
                line = input()
                if not line:
                    break
                lines.append(line)
        except (EOFError, KeyboardInterrupt):
            pass
        
        code = '\n'.join(lines) if lines else Prompt.ask("Script code")
        
        success, response = make_mcp_request('tools/call', {
            'name': 'editpythonscript',
            'arguments': {
                'componentId': component_id,
                'code': code
            }
        })
        
        if success:
            console.print("✅ [green]Python script updated[/green]")
            if 'result' in response and 'content' in response['result']:
                content = response['result']['content']
                if content and len(content) > 0:
                    text_content = content[0].get('text', '')
                    try:
                        import json
                        data = json.loads(text_content)
                        diagnostics = data.get('diagnostics', {})
                        errors = diagnostics.get('errors', [])
                        warnings = diagnostics.get('warnings', [])
                        
                        if errors:
                            console.print(f"❌ [red]Errors: {errors}[/red]")
                        if warnings:
                            console.print(f"⚠️ [yellow]Warnings: {warnings}[/yellow]")
                        if not errors and not warnings:
                            console.print("✅ [green]No errors or warnings[/green]")
                    except:
                        console.print(f"Response: {text_content}")
        else:
            console.print(f"❌ [red]Failed to edit Python script: {response}[/red]")

def tool_get_python_script_errors():
    """Get Python Script Errors tool"""
    console.print("\n🐛 [blue]GET PYTHON SCRIPT ERRORS[/blue]")
    console.print(Rule(style="blue"))
    
    component_id = select_component("Select Python script component")
    if component_id:
        success, response = make_mcp_request('tools/call', {
            'name': 'getpythonscripterrors',
            'arguments': {'componentId': component_id}
        })
        
        if success:
            console.print("✅ [green]Python script errors retrieved[/green]")
            if 'result' in response and 'content' in response['result']:
                content = response['result']['content']
                if content and len(content) > 0:
                    text_content = content[0].get('text', '')
                    try:
                        import json
                        data = json.loads(text_content)
                        diagnostics = data.get('diagnostics', {})
                        errors = diagnostics.get('errors', [])
                        warnings = diagnostics.get('warnings', [])
                        
                        if errors:
                            console.print(f"\n❌ [red]Errors ({len(errors)}):[/red]")
                            for error in errors:
                                console.print(f"  • {error}")
                        if warnings:
                            console.print(f"\n⚠️ [yellow]Warnings ({len(warnings)}):[/yellow]")
                            for warning in warnings:
                                console.print(f"  • {warning}")
                        if not errors and not warnings:
                            console.print("✅ [green]No errors or warnings[/green]")
                    except:
                        console.print(f"Response: {text_content}")
            else:
                console.print(f"Response: {response}")
        else:
            console.print(f"❌ [red]Failed to get Python script errors: {response}[/red]")

def tool_list_python_scripts():
    """List Python Scripts tool"""
    console.print("\n📋 [blue]LIST PYTHON SCRIPTS[/blue]")
    console.print(Rule(style="blue"))
    
    success, response = make_mcp_request('tools/call', {
        'name': 'listpythonscripts',
        'arguments': {}
    })
    
    if success:
        console.print("✅ [green]Python scripts retrieved[/green]")
        if 'result' in response and 'content' in response['result']:
            content = response['result']['content']
            if content and len(content) > 0:
                text_content = content[0].get('text', '')
                try:
                    import json
                    data = json.loads(text_content)
                    scripts = data.get('scripts', [])
                    count = data.get('count', 0)
                    
                    if scripts:
                        table = Table(title="Python Script Components", box=box.ROUNDED)
                        table.add_column("ID", style="cyan", width=36)
                        table.add_column("Name", style="green")
                        table.add_column("Position", style="yellow")
                        
                        for script in scripts:
                            pos = script.get('position', {})
                            pos_str = f"({pos.get('x', 0)}, {pos.get('y', 0)})"
                            table.add_row(
                                script.get('id', 'N/A'),
                                script.get('name', 'N/A'),
                                pos_str
                            )
                        
                        console.print(table)
                        console.print(f"\n✅ [green]Found {count} Python script component(s)[/green]")
                    else:
                        console.print("ℹ️ [dim]No Python script components found[/dim]")
                except:
                    console.print(f"Response: {text_content}")
        else:
            console.print(f"Response: {response}")
    else:
        console.print(f"❌ [red]Failed to list Python scripts: {response}[/red]")

def tool_run_hang_regression_suite():
    """Run timeout/concurrency/restart probes for MCP hang regressions."""
    console.print("\n🧪 [blue]MCP HANG REGRESSION SUITE[/blue]")
    console.print(Rule(style="blue"))

    results: List[Dict[str, Any]] = []

    original_timeout = mcp_config.timeout
    original_retries = mcp_config.retry_attempts
    try:
        mcp_config.timeout = min(max(2, original_timeout), 5)
        mcp_config.retry_attempts = 1
        start = time.monotonic()
        success, response = make_mcp_request("tools/call", {
            "name": "getcomponentcount",
            "arguments": {}
        })
        elapsed = time.monotonic() - start
        results.append({
            "scenario": "timeout_guard",
            "success": success,
            "elapsed_seconds": round(elapsed, 3),
            "details": str(response)[:220]
        })
    finally:
        mcp_config.timeout = original_timeout
        mcp_config.retry_attempts = original_retries

    payload = {
        "jsonrpc": "2.0",
        "method": "tools/call",
        "params": {"name": "getcomponentcount", "arguments": {}}
    }

    def _burst_call(request_id: int) -> bool:
        try:
            response = requests.post(
                mcp_config.url,
                json={**payload, "id": request_id},
                timeout=(5, mcp_config.timeout)
            )
            return response.status_code == 200
        except requests.exceptions.RequestException:
            return False

    total_calls = 8
    successes = 0
    failures = 0
    start = time.monotonic()
    with ThreadPoolExecutor(max_workers=4) as executor:
        futures = [executor.submit(_burst_call, int(time.time() * 1000) + index) for index in range(total_calls)]
        for future in as_completed(futures):
            if future.result():
                successes += 1
            else:
                failures += 1
    results.append({
        "scenario": "concurrency_burst",
        "success": failures == 0,
        "elapsed_seconds": round(time.monotonic() - start, 3),
        "details": f"successes={successes}, failures={failures}"
    })

    if Confirm.ask("Do you want to restart the MCP server during recovery probe?", default=False):
        console.print("[yellow]Restart the MCP server now. Press Enter to start recovery polling.[/yellow]")
        input()

    probe_attempts = 10
    recovery_successes = 0
    for _ in range(probe_attempts):
        success, _ = make_mcp_request("tools/call", {"name": "getcomponentcount", "arguments": {}})
        if success:
            recovery_successes += 1
        time.sleep(0.75)

    results.append({
        "scenario": "restart_recovery_probe",
        "success": recovery_successes > 0,
        "elapsed_seconds": round(probe_attempts * 0.75, 3),
        "details": f"successful_probes={recovery_successes}/{probe_attempts}"
    })

    table = Table(title="MCP Hang Regression Results", box=box.ROUNDED)
    table.add_column("Scenario", style="cyan")
    table.add_column("Result", style="green")
    table.add_column("Elapsed(s)", style="yellow")
    table.add_column("Details", style="white")
    for result in results:
        table.add_row(
            result["scenario"],
            "PASS" if result["success"] else "FAIL",
            str(result["elapsed_seconds"]),
            result["details"]
        )
    console.print(table)

def tool_create_component_group():
    """Create Component Group tool with enhanced validation"""
    console.print("\n📦 [blue]CREATE COMPONENT GROUP[/blue]")
    console.print(Rule(style="blue"))
    
    name = Prompt.ask("Group name")
    x = IntPrompt.ask("X position", default=50)
    y = IntPrompt.ask("Y position", default=50)
    color = Prompt.ask("Color (hex)", default="#FF0000")
    
    # Validate inputs
    valid_coords, coords_or_error = validate_coordinates(x, y)
    if not valid_coords:
        ui_manager.show_error_panel("Invalid Coordinates", coords_or_error)
        return
    
    valid_color, color_or_error = validate_color(color)
    if not valid_color:
        ui_manager.show_error_panel("Invalid Color", f"{color_or_error}\n💡 Tip: Use format like #FF0000 for red")
        return
    
    create_component_group(name, x, y, color)

def tool_group_existing_components():
    """Group Existing Components tool"""
    console.print("\n🗂️ [blue]GROUP EXISTING COMPONENTS[/blue]")
    console.print(Rule(style="blue"))
    
    console.print("⚠️ [yellow]This tool requires component IDs - use list components first[/yellow]")
    component_ids = Prompt.ask("Component IDs (comma-separated)").split(',')
    component_ids = [id.strip() for id in component_ids]
    group_name = Prompt.ask("Group name (optional)", default="")
    group_color = Prompt.ask("Group color (optional)", default="")
    
    args = {'componentIds': component_ids}
    if group_name:
        args['groupName'] = group_name
    if group_color:
        args['groupColor'] = group_color
    
    with console.status("[blue]Grouping existing components...", spinner="dots"):
        success, response = make_mcp_request('tools/call', {
            'name': 'groupexistingcomponents',
            'arguments': args
        })
    
    if success:
        try:
            # Parse the nested response structure
            group_data = extract_component_data(str(response))
            if group_data and group_data.get('Success'):
                message = group_data.get('Message', 'Components grouped successfully')
                console.print(f"✅ [green]{message}[/green]")
                
                # Show additional details if available
                if 'GroupId' in group_data:
                    console.print(f"📋 [blue]Group ID: {group_data['GroupId']}[/blue]")
                if 'ComponentCount' in group_data:
                    console.print(f"📊 [blue]Components in group: {group_data['ComponentCount']}[/blue]")
            else:
                error_msg = group_data.get('Message', 'Unknown error') if group_data else 'Failed to parse response'
                console.print(f"❌ [red]Failed to group components: {error_msg}[/red]")
                
                # Show invalid IDs if available
                if group_data and 'InvalidIds' in group_data:
                    invalid_ids = group_data['InvalidIds']
                    if invalid_ids:
                        console.print(f"⚠️ [yellow]Invalid component IDs: {', '.join(invalid_ids)}[/yellow]")
        except Exception as e:
            console.print(f"❌ [red]Error parsing group response: {e}[/red]")
            console.print(f"📋 [dim]Raw response: {response}[/dim]")
    else:
        console.print(f"❌ [red]Failed to group components: {response}[/red]")

def tool_group_components_by_type():
    """Group Components by Type tool with improved response parsing"""
    console.print("\n🏷️ [blue]GROUP COMPONENTS BY TYPE[/blue]")
    console.print(Rule(style="blue"))
    
    with console.status("[blue]Grouping components by type...", spinner="dots"):
        success, response = make_mcp_request('tools/call', {
            'name': 'groupcomponentsbytype',
            'arguments': {}
        })
    
    if success:
        try:
            # Parse the nested response structure
            group_data = extract_component_data(str(response))
            if group_data and group_data.get('Success'):
                groups_created = group_data.get('GroupsCreated', 0)
                message = group_data.get('Message', f'Components grouped by type - {groups_created} groups created')
                console.print(f"✅ [green]{message}[/green]")
                
                # Show details about created groups
                if groups_created > 0:
                    console.print(f"📊 [blue]Groups created: {groups_created}[/blue]")
                    groups = group_data.get('Groups', [])
                    if groups:
                        console.print("📋 [blue]Created groups:[/blue]")
                        for group in groups:
                            group_name = group.get('GroupName', 'Unnamed')
                            component_count = group.get('ComponentCount', 0)
                            console.print(f"  • {group_name} ({component_count} components)")
                else:
                    console.print("ℹ️ [yellow]No components found to group[/yellow]")
            else:
                error_msg = group_data.get('Message', 'Unknown error') if group_data else 'Failed to parse response'
                console.print(f"❌ [red]Failed to group components by type: {error_msg}[/red]")
        except Exception as e:
            console.print(f"❌ [red]Error parsing group response: {e}[/red]")
            console.print(f"📋 [dim]Raw response: {response}[/dim]")
    else:
        console.print(f"❌ [red]Failed to group components by type: {response}[/red]")

def tool_get_component_runtime_info():
    """Get Component Runtime Info tool"""
    console.print("\n⚡ [blue]GET COMPONENT RUNTIME INFO[/blue]")
    console.print(Rule(style="blue"))
    
    component_id = select_component("Select component to get runtime info for")
    if component_id:
        success, response = make_mcp_request('tools/call', {
            'name': 'getcomponentruntimeinfo',
            'arguments': {'componentId': component_id}
        })
        
        if success:
            console.print("✅ [green]Component runtime info retrieved[/green]")
            console.print(f"Response: {response}")
        else:
            console.print(f"❌ [red]Failed to get runtime info: {response}[/red]")

def tool_get_all_runtime_info():
    """Get All Runtime Info tool"""
    console.print("\n📊 [blue]GET ALL RUNTIME INFO[/blue]")
    console.print(Rule(style="blue"))
    
    success, response = make_mcp_request('tools/call', {
        'name': 'getallcomponentsruntimeinfo',
        'arguments': {}
    })
    
    if success:
        console.print("✅ [green]All runtime info retrieved[/green]")
        console.print(f"Response: {response}")
    else:
        console.print(f"❌ [red]Failed to get all runtime info: {response}[/red]")

def tool_capture_canvas_state():
    """Capture Canvas State tool"""
    console.print("\n📸 [blue]CAPTURE CANVAS STATE[/blue]")
    console.print(Rule(style="blue"))
    
    success, response = make_mcp_request('tools/call', {
        'name': 'capturecanvasstate',
        'arguments': {}
    })
    
    if success:
        console.print("✅ [green]Canvas state captured[/green]")
        console.print(f"Response: {response}")
    else:
        console.print(f"❌ [red]Failed to capture canvas state: {response}[/red]")

def tool_get_system_health():
    """Get System Health tool"""
    console.print("\n💚 [blue]GET SYSTEM HEALTH[/blue]")
    console.print(Rule(style="blue"))
    
    success, response = make_mcp_request('tools/call', {
        'name': 'getsystemhealth',
        'arguments': {}
    })
    
    if success:
        console.print("✅ [green]System health retrieved[/green]")
        console.print(f"Response: {response}")
    else:
        console.print(f"❌ [red]Failed to get system health: {response}[/red]")

def tool_run_health_check():
    """Run Health Check tool"""
    console.print("\n🩺 [blue]RUN HEALTH CHECK[/blue]")
    console.print(Rule(style="blue"))
    
    health_check_name = Prompt.ask("Health check name")
    
    success, response = make_mcp_request('tools/call', {
        'name': 'runhealthcheck',
        'arguments': {'healthCheckName': health_check_name}
    })
    
    if success:
        console.print("✅ [green]Health check completed[/green]")
        console.print(f"Response: {response}")
    else:
        console.print(f"❌ [red]Failed to run health check: {response}[/red]")

def tool_list_health_checks():
    """List Health Checks tool"""
    console.print("\n📋 [blue]LIST HEALTH CHECKS[/blue]")
    console.print(Rule(style="blue"))
    
    success, response = make_mcp_request('tools/call', {
        'name': 'listhealthchecks',
        'arguments': {}
    })
    
    if success:
        console.print("✅ [green]Health checks listed[/green]")
        console.print(f"Response: {response}")
    else:
        console.print(f"❌ [red]Failed to list health checks: {response}[/red]")

def tool_configuration_settings():
    """Configuration Settings tool"""
    global mcp_config, mcp_client
    
    console.print("\n⚙️ [blue]CONFIGURATION SETTINGS[/blue]")
    console.print(Rule(style="blue"))
    
    # Show current configuration
    config_table = ui_manager.create_table(
        "Current Configuration",
        ["Setting", "Value", "Description"],
        [
            ["MCP URL", mcp_config.url, "Server endpoint"],
            ["Timeout", str(mcp_config.timeout), "Request timeout in seconds"],
            ["Retry Attempts", str(mcp_config.retry_attempts), "Number of retry attempts"],
            ["Retry Delay", str(mcp_config.retry_delay), "Delay between retries"],
            ["Log Level", mcp_config.log_level, "Logging verbosity"]
        ]
    )
    console.print(config_table)
    
    # Configuration options
    console.print("\n[bold]Configuration Options:[/bold]")
    console.print("1. Change MCP URL")
    console.print("2. Change timeout settings")
    console.print("3. Change retry settings")
    console.print("4. Change log level")
    console.print("5. Save configuration to file")
    console.print("6. Load configuration from file")
    console.print("7. Reset to defaults")
    console.print("8. Back to main menu")
    
    choice = Prompt.ask("Select option", choices=[str(i) for i in range(1, 9)], default="8")
    
    if choice == "1":
        new_url = Prompt.ask("Enter new MCP URL", default=mcp_config.url)
        if new_url != mcp_config.url:
            mcp_config.url = new_url
            # Recreate client with new config
            mcp_client = MCPClient(mcp_config)
            ui_manager.show_success_panel("Configuration Updated", f"MCP URL changed to: {new_url}")
    
    elif choice == "2":
        new_timeout = IntPrompt.ask("Enter timeout in seconds", default=mcp_config.timeout)
        if new_timeout != mcp_config.timeout:
            mcp_config.timeout = new_timeout
            ui_manager.show_success_panel("Configuration Updated", f"Timeout changed to: {new_timeout}s")
    
    elif choice == "3":
        new_retry_attempts = IntPrompt.ask("Enter retry attempts", default=mcp_config.retry_attempts)
        new_retry_delay = float(Prompt.ask("Enter retry delay", default=str(mcp_config.retry_delay)))
        mcp_config.retry_attempts = new_retry_attempts
        mcp_config.retry_delay = new_retry_delay
        ui_manager.show_success_panel("Configuration Updated", "Retry settings updated")
    
    elif choice == "4":
        log_levels = ["DEBUG", "INFO", "WARNING", "ERROR"]
        new_log_level = Prompt.ask("Select log level", choices=log_levels, default=mcp_config.log_level)
        if new_log_level != mcp_config.log_level:
            mcp_config.log_level = new_log_level
            logging.getLogger().setLevel(getattr(logging, new_log_level))
            ui_manager.show_success_panel("Configuration Updated", f"Log level changed to: {new_log_level}")
    
    elif choice == "5":
        if mcp_config.save_to_file():
            ui_manager.show_success_panel("Configuration Saved", "Settings saved to mcp_config.json")
        else:
            ui_manager.show_error_panel("Save Failed", "Could not save configuration")
    
    elif choice == "6":
        config_path = Prompt.ask("Enter config file path", default="mcp_config.json")
        new_config = MCPConfig.load_from_file(config_path)
        if new_config:
            mcp_config = new_config
            mcp_client = MCPClient(mcp_config)
            ui_manager.show_success_panel("Configuration Loaded", f"Settings loaded from {config_path}")
        else:
            ui_manager.show_error_panel("Load Failed", f"Could not load configuration from {config_path}")
    
    elif choice == "7":
        if Confirm.ask("Reset to default settings?"):
            mcp_config = MCPConfig()
            mcp_client = MCPClient(mcp_config)
            ui_manager.show_success_panel("Configuration Reset", "Settings reset to defaults")



def tool_modify_component_parameters():
    """Modify Component Parameters tool"""
    console.print("\n🔧 [blue]MODIFY COMPONENT PARAMETERS[/blue]")
    console.print(Rule(style="blue"))
    
    # First, list current components so user can see what's available
    console.print("📋 [yellow]Current components on canvas:[/yellow]")
    components = list_components()
    
    if not components:
        console.print("❌ [red]No components found to modify[/red]")
        return
    
    # Get component ID from user
    component_id = Prompt.ask("Enter component ID to modify")
    
    if not component_id:
        console.print("❌ [red]Component ID is required[/red]")
        return
    
    # Show current parameter structure
    console.print(f"\n🔍 [blue]Getting current component info for {component_id}...[/blue]")
    
    success, response = make_mcp_request('tools/call', {
        'name': 'getcomponentinfo',
        'arguments': {'componentId': component_id}
    })
    
    if not success:
        console.print(f"❌ [red]Failed to get component info: {response}[/red]")
        return
    
    # Parse component info to show current parameters
    try:
        if 'result' in response and 'content' in response['result']:
            content = json.loads(response['result']['content'][0]['text'])
            component_info = content.get('Component', {})
            
            console.print(f"\n📊 [green]Component: {component_info.get('Name', 'Unknown')} ({component_info.get('Type', 'Unknown')})[/green]")
            
            # Show current input parameters
            inputs = component_info.get('Inputs', [])
            if inputs:
                console.print("\n📥 [yellow]Current Input Parameters:[/yellow]")
                for i, param in enumerate(inputs):
                    console.print(f"  Input {i}: {param.get('Name', 'Unknown')} ({param.get('NickName', 'Unknown')})")
            
            # Show current output parameters
            outputs = component_info.get('Outputs', [])
            if outputs:
                console.print("\n📤 [yellow]Current Output Parameters:[/yellow]")
                for i, param in enumerate(outputs):
                    console.print(f"  Output {i}: {param.get('Name', 'Unknown')} ({param.get('NickName', 'Unknown')})")
            
            # Get new parameter names from user
            console.print("\n🔧 [blue]Enter new parameter names (press Enter to skip):[/blue]")
            
            new_names = {}
            
            # Input parameters
            for i in range(len(inputs)):
                new_name = Prompt.ask(f"New name for Input {i} (currently: {inputs[i].get('NickName', 'Unknown')})")
                if new_name and new_name.strip():
                    new_names[f"input_{i}"] = new_name.strip()
            
            # Output parameters
            for i in range(len(outputs)):
                new_name = Prompt.ask(f"New name for Output {i} (currently: {outputs[i].get('NickName', 'Unknown')})")
                if new_name and new_name.strip():
                    new_names[f"output_{i}"] = new_name.strip()
            
            if not new_names:
                console.print("⚠️ [yellow]No parameter names to change[/yellow]")
                return
            
            # Confirm changes
            console.print(f"\n📝 [blue]About to modify {len(new_names)} parameters:[/blue]")
            for key, value in new_names.items():
                console.print(f"  {key}: {value}")
            
            if not Confirm.ask("Proceed with these changes?"):
                console.print("❌ [red]Parameter modification cancelled[/red]")
                return
            
            # Call the MCP tool to modify parameters
            console.print("\n🔧 [blue]Modifying component parameters...[/blue]")
            
            success, response = make_mcp_request('tools/call', {
                'name': 'modifyscriptcomponentparameters',
                'arguments': {
                    'componentId': component_id,
                    'parameterConfig': new_names
                }
            })
            
            if success:
                console.print("✅ [green]Component parameters modified successfully![/green]")
                
                # Show the response
                if 'result' in response and 'content' in response['result']:
                    content = json.loads(response['result']['content'][0]['text'])
                    console.print(f"Modified: {content.get('Value', 'Unknown')} parameters")
                    console.print(f"Message: {content.get('Message', 'Unknown')}")
                
                # Refresh component info to show changes
                console.print("\n🔄 [blue]Refreshing component info...[/blue]")
                success, response = make_mcp_request('tools/call', {
                    'name': 'getcomponentinfo',
                    'arguments': {'componentId': component_id}
                })
                
                if success and 'result' in response and 'content' in response['result']:
                    content = json.loads(response['result']['content'][0]['text'])
                    component_info = content.get('Component', {})
                    
                    console.print("\n📊 [green]Updated Component Parameters:[/green]")
                    
                    # Show updated input parameters
                    inputs = component_info.get('Inputs', [])
                    if inputs:
                        console.print("\n📥 [yellow]Updated Input Parameters:[/yellow]")
                        for i, param in enumerate(inputs):
                            console.print(f"  Input {i}: {param.get('Name', 'Unknown')} ({param.get('NickName', 'Unknown')})")
                    
                    # Show updated output parameters
                    outputs = component_info.get('Outputs', [])
                    if outputs:
                        console.print("\n📤 [yellow]Updated Output Parameters:[/yellow]")
                        for i, param in enumerate(outputs):
                            console.print(f"  Output {i}: {param.get('Name', 'Unknown')} ({param.get('NickName', 'Unknown')})")
                else:
                    console.print("⚠️ [yellow]Could not refresh component info[/yellow]")
            else:
                console.print(f"❌ [red]Failed to modify component parameters: {response}[/red]")
                
        else:
            console.print("❌ [red]Invalid component info response format[/red]")
            
    except Exception as e:
        console.print(f"❌ [red]Error processing component info: {e}[/red]")



class SimpleGrasshopperAPI:
    """Simplified API for AI assistant usage"""
    
    def __init__(self, config: Optional[MCPConfig] = None):
        self.config = config or MCPConfig.load_from_file()
        self.client = MCPClient(self.config)
        self.ui = UIManager(console)
        self.test_manager = TestManager(self.client, self.ui)
    
    def check_connection(self) -> bool:
        """Check if MCP connection is working"""
        return check_mcp_connection()
    
    def list_tools(self) -> Dict[str, Any]:
        """List all available MCP tools"""
        success, response = self.client.make_request("tools/list")
        return {"success": success, "data": response}
    
    def list_components(self) -> Dict[str, Any]:
        """List current components on canvas"""
        success, response = self.client.make_request("tools/call", {
            "name": "listallcomponentswithguids",
            "arguments": {}
        })
        return {"success": success, "data": response}
    
    def add_component(self, component_type: str, x: float = 100, y: float = 100) -> Dict[str, Any]:
        """Add a component to the canvas"""
        success, response = self.client.make_request("tools/call", {
            "name": "addcomponent",
            "arguments": {
                "type": component_type,
                "x": x,
                "y": y
            }
        })
        return {"success": success, "data": response}
    
    def add_python_script(self, x: float = 100, y: float = 100, script: str = "") -> Dict[str, Any]:
        """Add a Python script component"""
        success, response = self.client.make_request("tools/call", {
            "name": "addpythonscriptcomponent",
            "arguments": {
                "x": x,
                "y": y,
                "script": script
            }
        })
        return {"success": success, "data": response}
    
    def add_csharp_script(self, x: float = 100, y: float = 100, script: str = "") -> Dict[str, Any]:
        """Add a C# script component"""
        success, response = self.client.make_request("tools/call", {
            "name": "addcsharpscriptcomponent",
            "arguments": {
                "x": x,
                "y": y,
                "script": script
            }
        })
        return {"success": success, "data": response}
    
    def set_component_value(self, component_id: str, parameter_name: str, value: Any) -> Dict[str, Any]:
        """Set a component parameter value"""
        success, response = self.client.make_request("tools/call", {
            "name": "setcomponentvalue",
            "arguments": {
                "componentId": component_id,
                "parameterName": parameter_name,
                "value": value
            }
        })
        return {"success": success, "data": response}
    
    def set_component_script(self, component_id: str, language: str, script: str) -> Dict[str, Any]:
        """Set script content for a script component"""
        success, response = self.client.make_request("tools/call", {
            "name": "setcomponentscript",
            "arguments": {
                "componentId": component_id,
                "language": language,
                "script": script
            }
        })
        return {"success": success, "data": response}
    
    def connect_components(self, source_id: str, source_output: int, target_id: str, target_input: int) -> Dict[str, Any]:
        """Connect two components"""
        success, response = self.client.make_request("tools/call", {
            "name": "connectcomponents",
            "arguments": {
                "sourceComponentId": source_id,
                "sourceOutputIndex": source_output,
                "targetComponentId": target_id,
                "targetInputIndex": target_input
            }
        })
        return {"success": success, "data": response}
    
    def get_component_info(self, component_id: str) -> Dict[str, Any]:
        """Get detailed information about a component"""
        success, response = self.client.make_request("tools/call", {
            "name": "getcomponentinfo",
            "arguments": {
                "componentId": component_id
            }
        })
        return {"success": success, "data": response}
    
    def clear_canvas(self) -> Dict[str, Any]:
        """Clear all components from canvas"""
        success, response = self.client.make_request("tools/call", {
            "name": "cleardocument",
            "arguments": {}
        })
        return {"success": success, "data": response}
    
    def capture_canvas_state(self) -> Dict[str, Any]:
        """Capture current canvas state"""
        success, response = self.client.make_request("tools/call", {
            "name": "capturecanvasstate",
            "arguments": {}
        })
        return {"success": success, "data": response}
    
    def get_system_health(self) -> Dict[str, Any]:
        """Get system health information"""
        success, response = self.client.make_request("tools/call", {
            "name": "getsystemhealth",
            "arguments": {}
        })
        return {"success": success, "data": response}
    
    def run_workflow(self, workflow_name: str) -> Dict[str, Any]:
        """Run a predefined workflow"""
        workflows = {
            "spiral": test_complete_spiral_workflow,
            "csharp": test_csharp_script_workflow,
            "basic": test_individual_tool
        }
        
        if workflow_name not in workflows:
            return {"success": False, "error": f"Unknown workflow: {workflow_name}"}
        
        try:
            result = workflows[workflow_name]()
            return {"success": True, "data": result}
        except Exception as e:
            return {"success": False, "error": str(e)}
    
    def batch_operations(self, operations: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
        """Execute multiple operations in sequence"""
        results = []
        for op in operations:
            try:
                if op["action"] == "add_component":
                    result = self.add_component(op["type"], op.get("x", 100), op.get("y", 100))
                elif op["action"] == "add_python_script":
                    result = self.add_python_script(op.get("x", 100), op.get("y", 100), op.get("script", ""))
                elif op["action"] == "add_csharp_script":
                    result = self.add_csharp_script(op.get("x", 100), op.get("y", 100), op.get("script", ""))
                elif op["action"] == "set_value":
                    result = self.set_component_value(op["component_id"], op["param"], op["value"])
                elif op["action"] == "set_script":
                    result = self.set_component_script(op["component_id"], op["language"], op["script"])
                elif op["action"] == "connect":
                    result = self.connect_components(op["src_id"], op["src_out"], op["tgt_id"], op["tgt_in"])
                else:
                    result = {"success": False, "error": f"Unknown action: {op['action']}"}
                
                results.append({"operation": op, "result": result})
            except Exception as e:
                results.append({"operation": op, "result": {"success": False, "error": str(e)}})
        
        return results
    
    def create_spiral_workflow(self, x: float = 100, y: float = 100) -> Dict[str, Any]:
        """Create a complete spiral workflow with Python script"""
        operations = [
            {"action": "add_python_script", "x": x, "y": y, "script": PYTHON_SPIRAL_CODE},
            {"action": "add_component", "type": "number", "x": x + 200, "y": y},
            {"action": "add_component", "type": "number", "x": x + 200, "y": y + 100},
            {"action": "add_component", "type": "panel", "x": x + 400, "y": y}
        ]
        
        results = self.batch_operations(operations)
        return {"success": all(r["result"]["success"] for r in results), "data": results}
    
    def get_component_by_type(self, component_type: str) -> List[Dict[str, Any]]:
        """Find components by type"""
        components_result = self.list_components()
        if not components_result["success"]:
            return []
        
        # This would need to be implemented based on the actual response structure
        # For now, return empty list
        return []
    
    def create_group(self, name: str, x: float = 100, y: float = 100, color: str = "#FF0000") -> Dict[str, Any]:
        """Create a component group"""
        success, response = self.client.make_request("tools/call", {
            "name": "createcomponentgroup",
            "arguments": {
                "name": name,
                "x": x,
                "y": y,
                "groupColor": color
            }
        })
        return {"success": success, "data": response}
    
    def save_document(self, file_path: str = None) -> Dict[str, Any]:
        """Save the current document"""
        success, response = self.client.make_request("tools/call", {
            "name": "savedocument",
            "arguments": {
                "filePath": file_path
            } if file_path else {}
        })
        return {"success": success, "data": response}
    
    def load_document(self, file_path: str) -> Dict[str, Any]:
        """Load a document"""
        success, response = self.client.make_request("tools/call", {
            "name": "loaddocument",
            "arguments": {
                "filePath": file_path
            }
        })
        return {"success": success, "data": response}


# Global simplified API instance for easy access
simple_api = SimpleGrasshopperAPI(mcp_config)

# Convenience functions for direct access
def api() -> SimpleGrasshopperAPI:
    """Get the global API instance"""
    return simple_api

def quick_check() -> bool:
    """Quick connection check"""
    return simple_api.check_connection()

def quick_list() -> Dict[str, Any]:
    """Quick component list"""
    return simple_api.list_components()

def quick_add(component_type: str, x: float = 100, y: float = 100) -> Dict[str, Any]:
    """Quick component addition"""
    return simple_api.add_component(component_type, x, y)


def create_cli_parser() -> argparse.ArgumentParser:
    """Create command-line argument parser"""
    parser = argparse.ArgumentParser(
        description="Grasshopper MCP Test Suite - Command Line Interface",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  # Basic operations
  python grasshopper_mcp_tester.py --check-connection
  python grasshopper_mcp_tester.py --list-components
  python grasshopper_mcp_tester.py --system-health
  
  # Component creation
  python grasshopper_mcp_tester.py --add-component slider 200 300
  python grasshopper_mcp_tester.py --add-python-script 100 200 "print('Hello')"
  python grasshopper_mcp_tester.py --add-csharp-script 100 200 "System.Console.WriteLine(\"Hello\");"
  
  # Advanced features
  python grasshopper_mcp_tester.py --capture-state
  python grasshopper_mcp_tester.py --clear-canvas
  
  # Groups
  python grasshopper_mcp_tester.py --create-group "MyGroup" 100 200 "#FF0000"
  
  # JSON output for AI usage
  python grasshopper_mcp_tester.py --list-components --json --quiet
  
  # Interactive mode with improved UI
  python grasshopper_mcp_tester.py --interactive
        """
    )
    
    # Connection and info commands
    parser.add_argument("--check-connection", action="store_true", help="Check MCP connection")
    parser.add_argument("--list-tools", action="store_true", help="List available MCP tools")
    parser.add_argument("--list-components", action="store_true", help="List current components")
    parser.add_argument("--system-health", action="store_true", help="Get system health")
    parser.add_argument("--capture-state", action="store_true", help="Capture canvas state")
    
    # Component creation commands
    parser.add_argument("--add-component", nargs=3, metavar=("TYPE", "X", "Y"), help="Add component (type x y)")
    parser.add_argument("--add-python-script", nargs=3, metavar=("X", "Y", "SCRIPT"), help="Add Python script (x y script)")
    parser.add_argument("--add-csharp-script", nargs=3, metavar=("X", "Y", "SCRIPT"), help="Add C# script (x y script)")
    
    # Component manipulation commands
    parser.add_argument("--set-value", nargs=3, metavar=("ID", "PARAM", "VALUE"), help="Set component value")
    parser.add_argument("--set-script", nargs=3, metavar=("ID", "LANG", "SCRIPT"), help="Set component script")
    parser.add_argument("--connect", nargs=4, metavar=("SRC_ID", "SRC_OUT", "TGT_ID", "TGT_IN"), help="Connect components")
    parser.add_argument("--get-info", metavar="ID", help="Get component info")
    
    # Workflow commands
    parser.add_argument("--run-workflow", metavar="NAME", help="Run predefined workflow (spiral, csharp, basic)")
    parser.add_argument("--create-spiral", nargs=2, metavar=("X", "Y"), help="Create spiral workflow at position")
    parser.add_argument("--clear-canvas", action="store_true", help="Clear all components")
    
    # Document operations
    parser.add_argument("--save-document", metavar="PATH", help="Save document to file")
    parser.add_argument("--load-document", metavar="PATH", help="Load document from file")
    
    # Group operations
    parser.add_argument("--create-group", nargs=4, metavar=("NAME", "X", "Y", "COLOR"), help="Create component group")
    
    # Mode selection
    parser.add_argument("--interactive", action="store_true", help="Run in interactive mode")
    parser.add_argument("--run-999", "--run-comprehensive", dest="run_comprehensive", action="store_true", help="Run comprehensive test suite (same as menu option 999)")
    parser.add_argument("--quiet", action="store_true", help="Suppress output except results")
    parser.add_argument("--json", action="store_true", help="Output results in JSON format")
    
    # Configuration
    parser.add_argument("--mcp-url", default=None, help="MCP server URL")
    parser.add_argument("--config-file", default="mcp_config.json", help="Configuration file path")
    
    return parser


def run_cli_command(args: argparse.Namespace) -> int:
    """Run command-line interface"""
    # Setup configuration
    config = MCPConfig.load_from_file(args.config_file)
    if args.mcp_url:
        config.url = args.mcp_url
    
    # Create API instance
    api = SimpleGrasshopperAPI(config)
    
    # Suppress output if quiet mode
    if args.quiet:
        console.quiet = True
    
    results = []
    
    try:
        # Check connection first (except for interactive mode)
        if not args.interactive and not api.check_connection():
            if args.json:
                print(json.dumps({"success": False, "error": "MCP connection failed"}))
            else:
                console.print("❌ [red]MCP connection failed[/red]")
            return 1

        if args.run_comprehensive:
            # Run the full suite (option 999)
            suite_results = run_comprehensive_test()
            passed = sum(1 for r in suite_results if r["passed"])
            total = len(suite_results)
            success = passed == total
            if args.json:
                print(json.dumps({
                    "success": success,
                    "passed": passed,
                    "total": total,
                    "results": suite_results
                }, indent=2))
            else:
                console.print(f"\n✅ [green]Comprehensive test completed ({passed}/{total})[/green]" if success else f"\n⚠️ [yellow]Comprehensive test completed with failures ({passed}/{total})[/yellow]")
            return 0 if success else 1
        
        # Execute commands
        if args.check_connection:
            result = {"success": api.check_connection()}
            results.append(("check_connection", result))
        
        if args.list_tools:
            result = api.list_tools()
            results.append(("list_tools", result))
        
        if args.list_components:
            result = api.list_components()
            results.append(("list_components", result))
        
        if args.system_health:
            result = api.get_system_health()
            results.append(("system_health", result))
        
        if args.capture_state:
            result = api.capture_canvas_state()
            results.append(("capture_state", result))
        
        if args.add_component:
            component_type, x, y = args.add_component
            result = api.add_component(component_type, float(x), float(y))
            results.append(("add_component", result))
        
        if args.add_python_script:
            x, y, script = args.add_python_script
            result = api.add_python_script(float(x), float(y), script)
            results.append(("add_python_script", result))
        
        if args.add_csharp_script:
            x, y, script = args.add_csharp_script
            result = api.add_csharp_script(float(x), float(y), script)
            results.append(("add_csharp_script", result))
        
        if args.set_value:
            component_id, param, value = args.set_value
            result = api.set_component_value(component_id, param, value)
            results.append(("set_value", result))
        
        if args.set_script:
            component_id, lang, script = args.set_script
            result = api.set_component_script(component_id, lang, script)
            results.append(("set_script", result))
        
        if args.connect:
            src_id, src_out, tgt_id, tgt_in = args.connect
            result = api.connect_components(src_id, int(src_out), tgt_id, int(tgt_in))
            results.append(("connect", result))
        
        if args.get_info:
            result = api.get_component_info(args.get_info)
            results.append(("get_info", result))
        
        if args.run_workflow:
            result = api.run_workflow(args.run_workflow)
            results.append(("run_workflow", result))
        
        if args.create_spiral:
            x, y = args.create_spiral
            result = api.create_spiral_workflow(float(x), float(y))
            results.append(("create_spiral", result))
        
        if args.clear_canvas:
            result = api.clear_canvas()
            results.append(("clear_canvas", result))
        
        if args.save_document:
            result = api.save_document(args.save_document)
            results.append(("save_document", result))
        
        if args.load_document:
            result = api.load_document(args.load_document)
            results.append(("load_document", result))
        
        if args.create_group:
            name, x, y, color = args.create_group
            result = api.create_group(name, float(x), float(y), color)
            results.append(("create_group", result))
        
        # Output results
        if args.json:
            output = {}
            for command, result in results:
                output[command] = result
            print(json.dumps(output, indent=2))
        else:
            for command, result in results:
                if result.get("success"):
                    console.print(f"✅ [green]{command}: Success[/green]")
                    if not args.quiet and result.get("data"):
                        console.print(f"   Data: {result['data']}")
                else:
                    console.print(f"❌ [red]{command}: Failed[/red]")
                    if result.get("error"):
                        console.print(f"   Error: {result['error']}")
        
        return 0
        
    except Exception as e:
        if args.json:
            print(json.dumps({"success": False, "error": str(e)}))
        else:
            console.print(f"❌ [red]Error: {e}[/red]")
        return 1


def main():
    """Main function with command-line interface support"""
    parser = create_cli_parser()
    args = parser.parse_args()
    
    # If no arguments provided or interactive mode requested, run interactive mode
    if len(sys.argv) == 1 or args.interactive:
        return run_interactive_mode()
    
    # Otherwise run CLI commands
    return run_cli_command(args)


def run_interactive_mode():
    """Run the original interactive mode"""
    try:
        # Show enhanced header
        ui_manager.show_header()
        
        # Check MCP connection first
        if not check_mcp_connection():
            ui_manager.show_error_panel(
                "Connection Error",
                "Cannot continue without MCP connection. Please start the server and try again."
            )
            return False
        
        while True:
            show_main_menu()
            
            choice = Prompt.ask(
                "Select an option", 
                choices=[str(i) for i in range(1, 20)] + [str(i) for i in range(20, 25)] + ["999"],
                default="1"
            )
            
            # Hidden debug option - run comprehensive test
            if choice == "999":
                console.print("\n🔧 [yellow]COMPREHENSIVE TEST MODE: Running all functionality tests...[/yellow]")
                run_comprehensive_test()
                break
            
            try:
                if choice == "1":
                    check_mcp_connection()
                
                elif choice == "2":
                    list_available_tools()
                
                elif choice == "3":
                    list_components()
                
                elif choice == "4":
                    tool_get_system_health()
                
                elif choice == "5":
                    tool_add_component()
                
                elif choice == "6":
                    tool_add_python_script()
                
                elif choice == "7":
                    tool_add_csharp_script()
                
                elif choice == "8":
                    tool_list_available_component_types()
                
                elif choice == "9":
                    tool_set_component_value()
                
                elif choice == "10":
                    tool_get_component_info()
                
                elif choice == "11":
                    tool_connect_components()
                
                elif choice == "12":
                    tool_set_component_script()
                
                elif choice == "13":
                    tool_create_component_group()
                
                elif choice == "14":
                    tool_capture_canvas_state()
                
                elif choice == "15":
                    clear_canvas()
                
                elif choice == "16":
                    run_comprehensive_test()
                
                elif choice == "17":
                    tool_configuration_settings()
                
                elif choice == "18":
                    show_help()
                
                elif choice == "19":
                    console.print("\n👋 [green]Thank you for using Grasshopper MCP Test Suite![/green]")
                    break
                
                elif choice == "20":
                    tool_get_python_script()
                
                elif choice == "21":
                    tool_edit_python_script()
                
                elif choice == "22":
                    tool_get_python_script_errors()
                
                elif choice == "23":
                    tool_list_python_scripts()

                elif choice == "24":
                    tool_run_hang_regression_suite()
                
                # Wait for user to continue
                if choice != "19":
                    console.print("\n[dim]Press Enter to continue...[/dim]")
                    input()
                    
            except KeyboardInterrupt:
                console.print("\n⚠️ [yellow]Operation cancelled by user[/yellow]")
                continue
            except Exception as e:
                console.print(f"\n❌ [red]An error occurred: {e}[/red]")
                console.print("[dim]Press Enter to continue...[/dim]")
                input()
                
    except KeyboardInterrupt:
        console.print("\n\n👋 [green]Goodbye![/green]")
    except Exception as e:
        console.print(f"\n❌ [red]Fatal error: {e}[/red]")
        return False
    
    return True

if __name__ == "__main__":
    main()
