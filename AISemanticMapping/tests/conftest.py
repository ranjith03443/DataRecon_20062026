"""
Pytest configuration for the Semantic Intelligence Platform test suite.
"""
import asyncio
import pytest


@pytest.fixture(scope="session")
def event_loop():
    """Create an event loop for the test session."""
    policy = asyncio.get_event_loop_policy()
    loop = policy.new_event_loop()
    yield loop
    loop.close()
