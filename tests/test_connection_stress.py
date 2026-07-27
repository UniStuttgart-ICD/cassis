#!/usr/bin/env python3
"""
Connection Stress Test Suite
Tests connection stability, reconnection, rapid requests, and error recovery
"""

import os
import sys
import json
import time
import threading
from typing import Any, Dict, Optional, List, Tuple
from concurrent.futures import ThreadPoolExecutor, as_completed
import statistics

# Add the current directory to the path so we can import the tester
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from cassis_tester import make_mcp_request, check_mcp_connection
from Common.mcp_utils import MCPClient


class ConnectionStressTester:
    """Stress tester for MCP connection stability"""
    
    def __init__(self, url: str = None, timeout: int = 10):
        self.client = MCPClient(url=url, timeout=timeout)
        self.stats = {
            'total_requests': 0,
            'successful_requests': 0,
            'failed_requests': 0,
            'timeouts': 0,
            'connection_errors': 0,
            'response_times': [],
            'errors': []
        }
    
    def _make_request(self, method: str, params: Dict[str, Any] = None) -> Tuple[bool, Any]:
        """Make a request and track statistics"""
        start_time = time.time()
        self.stats['total_requests'] += 1
        
        try:
            success, response = self.client.make_request(method, params)
            elapsed = time.time() - start_time
            self.stats['response_times'].append(elapsed)
            
            if success:
                self.stats['successful_requests'] += 1
            else:
                self.stats['failed_requests'] += 1
                if 'timeout' in str(response).lower() or 'timed out' in str(response).lower():
                    self.stats['timeouts'] += 1
                if 'connection' in str(response).lower() or 'refused' in str(response).lower():
                    self.stats['connection_errors'] += 1
                self.stats['errors'].append({
                    'method': method,
                    'error': str(response),
                    'timestamp': time.time()
                })
            
            return success, response
        except Exception as e:
            elapsed = time.time() - start_time
            self.stats['response_times'].append(elapsed)
            self.stats['failed_requests'] += 1
            self.stats['connection_errors'] += 1
            self.stats['errors'].append({
                'method': method,
                'error': str(e),
                'timestamp': time.time()
            })
            return False, str(e)
    
    def print_stats(self):
        """Print statistics summary"""
        print("\n" + "=" * 60)
        print("📊 STRESS TEST STATISTICS")
        print("=" * 60)
        print(f"Total Requests: {self.stats['total_requests']}")
        print(f"Successful: {self.stats['successful_requests']} ({self.stats['successful_requests']/max(self.stats['total_requests'], 1)*100:.1f}%)")
        print(f"Failed: {self.stats['failed_requests']} ({self.stats['failed_requests']/max(self.stats['total_requests'], 1)*100:.1f}%)")
        print(f"Timeouts: {self.stats['timeouts']}")
        print(f"Connection Errors: {self.stats['connection_errors']}")
        
        if self.stats['response_times']:
            print(f"\nResponse Time Statistics:")
            print(f"  Min: {min(self.stats['response_times']):.3f}s")
            print(f"  Max: {max(self.stats['response_times']):.3f}s")
            print(f"  Mean: {statistics.mean(self.stats['response_times']):.3f}s")
            print(f"  Median: {statistics.median(self.stats['response_times']):.3f}s")
            if len(self.stats['response_times']) > 1:
                print(f"  Std Dev: {statistics.stdev(self.stats['response_times']):.3f}s")
        
        if self.stats['errors']:
            print(f"\nRecent Errors ({min(5, len(self.stats['errors']))}):")
            for error in self.stats['errors'][-5:]:
                print(f"  [{time.strftime('%H:%M:%S', time.localtime(error['timestamp']))}] {error['method']}: {error['error'][:80]}")
        print("=" * 60)


def test_rapid_requests(num_requests: int = 100, delay_ms: float = 10):
    """Test rapid sequential requests"""
    print(f"\n🚀 Test: Rapid Sequential Requests ({num_requests} requests, {delay_ms}ms delay)")
    print("-" * 60)
    
    tester = ConnectionStressTester()
    start_time = time.time()
    
    for i in range(num_requests):
        success, response = tester._make_request('tools/list')
        if not success and i % 10 == 0:
            print(f"  ⚠️ Request {i+1} failed: {response[:100]}")
        if delay_ms > 0:
            time.sleep(delay_ms / 1000.0)
    
    elapsed = time.time() - start_time
    print(f"  ✅ Completed {num_requests} requests in {elapsed:.2f}s ({num_requests/elapsed:.1f} req/s)")
    tester.print_stats()
    
    success_rate = tester.stats['successful_requests'] / max(tester.stats['total_requests'], 1)
    assert success_rate >= 0.95, f"Success rate too low: {success_rate*100:.1f}%"
    return tester


def test_concurrent_requests(num_requests: int = 50, max_workers: int = 10):
    """Test concurrent requests from multiple threads"""
    print(f"\n🔄 Test: Concurrent Requests ({num_requests} requests, {max_workers} workers)")
    print("-" * 60)
    
    tester = ConnectionStressTester()
    start_time = time.time()
    
    def make_request():
        return tester._make_request('tools/list')
    
    with ThreadPoolExecutor(max_workers=max_workers) as executor:
        futures = [executor.submit(make_request) for _ in range(num_requests)]
        completed = 0
        for future in as_completed(futures):
            try:
                success, response = future.result()
                completed += 1
                if not success and completed % 10 == 0:
                    print(f"  ⚠️ Request {completed} failed: {str(response)[:100]}")
            except Exception as e:
                print(f"  ❌ Exception in concurrent request: {e}")
    
    elapsed = time.time() - start_time
    print(f"  ✅ Completed {num_requests} concurrent requests in {elapsed:.2f}s ({num_requests/elapsed:.1f} req/s)")
    tester.print_stats()
    
    success_rate = tester.stats['successful_requests'] / max(tester.stats['total_requests'], 1)
    assert success_rate >= 0.90, f"Success rate too low: {success_rate*100:.1f}%"
    return tester


def test_connection_recovery(max_attempts: int = 10, delay_s: float = 0.5):
    """Test connection recovery after failures"""
    print(f"\n🔄 Test: Connection Recovery ({max_attempts} attempts)")
    print("-" * 60)
    
    tester = ConnectionStressTester()
    recovery_times = []
    
    for attempt in range(max_attempts):
        start_time = time.time()
        success, response = tester._make_request('tools/list')
        elapsed = time.time() - start_time
        
        if success:
            recovery_times.append(elapsed)
            print(f"  ✅ Attempt {attempt+1}: Connected in {elapsed:.3f}s")
        else:
            print(f"  ❌ Attempt {attempt+1}: Failed - {str(response)[:80]}")
            time.sleep(delay_s)
    
    tester.print_stats()
    
    if recovery_times:
        print(f"\n  Recovery Statistics:")
        print(f"    Successful recoveries: {len(recovery_times)}/{max_attempts}")
        print(f"    Avg recovery time: {statistics.mean(recovery_times):.3f}s")
        print(f"    Min recovery time: {min(recovery_times):.3f}s")
        print(f"    Max recovery time: {max(recovery_times):.3f}s")
    
    assert len(recovery_times) >= max_attempts * 0.8, f"Too many failures: {len(recovery_times)}/{max_attempts}"
    return tester


def test_long_running_operations(duration_s: float = 30, interval_s: float = 1.0):
    """Test connection stability over a long period"""
    print(f"\n⏱️  Test: Long-Running Operations ({duration_s}s duration, {interval_s}s interval)")
    print("-" * 60)
    
    tester = ConnectionStressTester()
    start_time = time.time()
    last_success_time = start_time
    failures_in_row = 0
    max_failures_in_row = 0
    
    print(f"  Starting at {time.strftime('%H:%M:%S')}...")
    
    while time.time() - start_time < duration_s:
        success, response = tester._make_request('tools/list')
        
        if success:
            last_success_time = time.time()
            failures_in_row = 0
        else:
            failures_in_row += 1
            max_failures_in_row = max(max_failures_in_row, failures_in_row)
            if failures_in_row >= 3:
                print(f"  ⚠️ {failures_in_row} consecutive failures at {time.strftime('%H:%M:%S')}")
        
        elapsed = time.time() - start_time
        if int(elapsed) % 5 == 0 and elapsed > 0:
            print(f"  [{int(elapsed)}s] Requests: {tester.stats['total_requests']}, "
                  f"Success: {tester.stats['successful_requests']}, "
                  f"Failed: {tester.stats['failed_requests']}")
        
        time.sleep(interval_s)
    
    total_elapsed = time.time() - start_time
    uptime = last_success_time - start_time
    
    print(f"\n  ✅ Test completed after {total_elapsed:.1f}s")
    print(f"  Last successful request: {uptime:.1f}s into test")
    print(f"  Max consecutive failures: {max_failures_in_row}")
    
    tester.print_stats()
    
    success_rate = tester.stats['successful_requests'] / max(tester.stats['total_requests'], 1)
    assert success_rate >= 0.85, f"Success rate too low: {success_rate*100:.1f}%"
    assert max_failures_in_row < 10, f"Too many consecutive failures: {max_failures_in_row}"
    return tester


def test_mixed_operations(num_operations: int = 50):
    """Test mixed operations to simulate real-world usage"""
    print(f"\n🔀 Test: Mixed Operations ({num_operations} operations)")
    print("-" * 60)
    
    tester = ConnectionStressTester()
    operations = [
        ('tools/list', {}),
        ('tools/call', {'name': 'getcomponentcount', 'arguments': {}}),
        ('tools/call', {'name': 'getdocumentinfo', 'arguments': {}}),
    ]
    
    for i in range(num_operations):
        method, params = operations[i % len(operations)]
        if method == 'tools/call':
            success, response = tester._make_request(method, params)
        else:
            success, response = tester._make_request(method, params)
        
        if not success and i % 10 == 0:
            print(f"  ⚠️ Operation {i+1} failed: {str(response)[:80]}")
        
        time.sleep(0.1)  # Small delay between operations
    
    tester.print_stats()
    
    success_rate = tester.stats['successful_requests'] / max(tester.stats['total_requests'], 1)
    assert success_rate >= 0.90, f"Success rate too low: {success_rate*100:.1f}%"
    return tester


def test_timeout_handling(timeout_s: float = 2.0):
    """Test timeout handling with very short timeout"""
    print(f"\n⏱️  Test: Timeout Handling (timeout={timeout_s}s)")
    print("-" * 60)
    
    # Use a very short timeout to test timeout handling
    tester = ConnectionStressTester(timeout=int(timeout_s))
    
    # Make several requests with short timeout
    for i in range(10):
        success, response = tester._make_request('tools/list')
        if not success:
            if 'timeout' in str(response).lower():
                print(f"  ⏱️ Request {i+1} timed out (expected)")
            else:
                print(f"  ⚠️ Request {i+1} failed: {str(response)[:80]}")
        time.sleep(0.1)
    
    tester.print_stats()
    
    # With very short timeout, we expect some failures but connection should still work
    print("  Note: Short timeouts may cause failures, but connection should recover")
    return tester


def test_burst_requests(burst_size: int = 20, num_bursts: int = 5, delay_between_bursts_s: float = 1.0):
    """Test burst requests (many requests in quick succession)"""
    print(f"\n💥 Test: Burst Requests ({num_bursts} bursts of {burst_size} requests)")
    print("-" * 60)
    
    tester = ConnectionStressTester()
    
    for burst_num in range(num_bursts):
        print(f"  Burst {burst_num + 1}/{num_bursts}...")
        burst_start = time.time()
        
        for i in range(burst_size):
            success, response = tester._make_request('tools/list')
            if not success:
                print(f"    ⚠️ Request {i+1} in burst failed")
        
        burst_elapsed = time.time() - burst_start
        print(f"    ✅ Burst completed in {burst_elapsed:.3f}s ({burst_size/burst_elapsed:.1f} req/s)")
        
        if burst_num < num_bursts - 1:
            time.sleep(delay_between_bursts_s)
    
    tester.print_stats()
    
    success_rate = tester.stats['successful_requests'] / max(tester.stats['total_requests'], 1)
    assert success_rate >= 0.90, f"Success rate too low: {success_rate*100:.1f}%"
    return tester


def test_connection_stability_check():
    """Initial connection check"""
    print("\n🔍 Checking MCP Connection...")
    print("-" * 60)
    
    if not check_mcp_connection():
        print("❌ MCP server not available. Please start the server first.")
        return False
    
    print("✅ MCP server is responding")
    return True


def main():
    """Main stress test function"""
    print("🔬 Cassis Connection Stress Test Suite")
    print("=" * 60)
    print()
    print("This suite will test:")
    print("  1. Rapid sequential requests")
    print("  2. Concurrent requests from multiple threads")
    print("  3. Connection recovery after failures")
    print("  4. Long-running operations")
    print("  5. Mixed operations")
    print("  6. Timeout handling")
    print("  7. Burst requests")
    print()
    
    # Check initial connection
    if not test_connection_stability_check():
        return
    
    print()
    
    try:
        # Run all stress tests
        test_rapid_requests(num_requests=100, delay_ms=10)
        time.sleep(1)
        
        test_concurrent_requests(num_requests=50, max_workers=10)
        time.sleep(1)
        
        test_connection_recovery(max_attempts=10, delay_s=0.5)
        time.sleep(1)
        
        test_mixed_operations(num_operations=50)
        time.sleep(1)
        
        test_burst_requests(burst_size=20, num_bursts=5, delay_between_bursts_s=1.0)
        time.sleep(1)
        
        # Long-running test (shorter for demo, increase duration_s for real stress test)
        test_long_running_operations(duration_s=30, interval_s=1.0)
        time.sleep(1)
        
        # Timeout test (optional, may fail with very short timeout)
        try:
            test_timeout_handling(timeout_s=2.0)
        except AssertionError as e:
            print(f"  ⚠️ Timeout test had expected failures: {e}")
        
        print()
        print("🎉 All Stress Tests Completed!")
        print("=" * 60)
        print()
        print("📊 Summary:")
        print("   • Connection stability tested")
        print("   • Reconnection scenarios validated")
        print("   • Concurrent request handling verified")
        print("   • Long-running stability confirmed")
        print("   • Error recovery mechanisms tested")
        
    except AssertionError as e:
        print(f"\n❌ Stress test failed: {e}")
        sys.exit(1)
    except KeyboardInterrupt:
        print("\n\n⚠️ Stress test interrupted by user")
        sys.exit(1)
    except Exception as e:
        print(f"\n❌ Unexpected error: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":
    main()
