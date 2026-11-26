"""Pair a dedicated Demo Bot with a user-supplied one-time code; no CLI input."""
import argparse
import asyncio
import hashlib
import json
import logging
import os
from pathlib import Path
import time

from lark_oapi import EventDispatcherHandler, LogLevel
from feishu_async_ws import AsyncFeishuWsClient as Client
from feishu_single_window_demo import read_credentials, ReceiverOwnerLock, receiver_owner_path


def match_pair(message, code, started_ms):
    try:
        if message.chat_type != 'p2p' or int(message.create_time) < started_ms:
            return None
        if message.message_type != 'text' or json.loads(message.content).get('text', '').strip() != code:
            return None
        return str(message.chat_id) if message.chat_id else None
    except (AttributeError, TypeError, ValueError):
        return None


async def pair(app_id, secret, code, result_path, timeout):
    loop = asyncio.get_running_loop()
    selected = loop.create_future()
    started_ms = int(time.time() * 1000)

    def receive(data):
        try:
            if data.event.sender.sender_type != 'user':
                return
            chat = match_pair(data.event.message, code, started_ms)
            if chat:
                loop.call_soon_threadsafe(accept, chat)
        except AttributeError:
            return

    def accept(chat):
        if not selected.done():
            selected.set_result(chat)

    handler = EventDispatcherHandler.builder('', '', level=LogLevel.ERROR).register_p2_im_message_receive_v1(receive).build()
    client = Client(app_id, secret, event_handler=handler, log_level=LogLevel.ERROR, auto_reconnect=False)
    tasks = []
    try:
        await asyncio.wait_for(client._connect(), timeout=20)
        tasks = [client.receive_task, asyncio.create_task(client._ping_loop())]
        completed, _ = await asyncio.wait([selected, client.receive_task],
            timeout=timeout, return_when=asyncio.FIRST_COMPLETED)
        if selected not in completed:
            if client.receive_task in completed:
                await client.receive_task
            raise TimeoutError('pairing_not_received')
        chat = selected.result()
        result_path.parent.mkdir(parents=True, exist_ok=True)
        staging = result_path.with_suffix('.tmp')
        staging.write_text(json.dumps({'app_id': app_id, 'chat_id': chat,
            'pairing_code_sha256': hashlib.sha256(code.encode()).hexdigest()}), encoding='utf-8')
        os.replace(staging, result_path)
    finally:
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        await client._disconnect()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--credentials', type=Path, required=True)
    parser.add_argument('--expected-app-id', required=True)
    parser.add_argument('--code', required=True)
    parser.add_argument('--result', type=Path, required=True)
    parser.add_argument('--timeout', type=int, default=120)
    args = parser.parse_args()
    logging.disable(logging.CRITICAL)
    try:
        app_id, secret = read_credentials(args.credentials)
        if app_id != args.expected_app_id or not args.code.startswith('CCPAD-') or len(args.code) < 18:
            raise ValueError('pairing_identity_mismatch')
        owner = ReceiverOwnerLock(receiver_owner_path(app_id))
        try:
            asyncio.run(pair(app_id, secret, args.code, args.result, args.timeout))
        finally:
            owner.close()
        return 0
    except Exception as error:
        print('Pairing failed: ' + type(error).__name__)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
