"""Source-bound native Windows advisory runtime admission (no financial authority)."""
import sys


def validate_platform(platform):
    if platform != 'win32':
        raise ValueError('native Windows required')
