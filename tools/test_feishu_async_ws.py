import asyncio
import unittest
from unittest.mock import AsyncMock, patch
import lark_oapi.ws.client as sdk
from feishu_async_ws import AsyncFeishuWsClient


class AsyncSdkTests(unittest.IsolatedAsyncioTestCase):
    async def test_installed_sdk_dispatches_on_current_loop_with_one_reader(self):
        frames = asyncio.Queue()
        received = asyncio.Event()
        connection = AsyncMock()
        connection.recv.side_effect = frames.get
        client = AsyncFeishuWsClient('fixture-app', 'fixture-secret', auto_reconnect=False)
        client._get_conn_url = lambda: f'wss://fixture.invalid/?{sdk.DEVICE_ID}=fixture&{sdk.SERVICE_ID}=1'
        async def handle(frame):
            self.assertEqual(frame, b'fixture-frame')
            received.set()
        client._handle_message = handle
        with patch.object(sdk.websockets, 'connect', AsyncMock(return_value=connection)):
            await client._connect()
            self.assertIs(client.receive_task.get_loop(), asyncio.get_running_loop())
            readers = [task for task in asyncio.all_tasks()
                       if '_receive_message_loop' in task.get_coro().__qualname__]
            self.assertEqual(len(readers), 1)
            await frames.put(b'fixture-frame')
            await asyncio.wait_for(received.wait(), timeout=1)
            client.receive_task.cancel()
            await asyncio.gather(client.receive_task, return_exceptions=True)
            await client._disconnect()
            connection.close.assert_awaited_once()


if __name__ == '__main__':
    unittest.main()
