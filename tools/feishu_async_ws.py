"""Run the installed Feishu SDK's single reader on the sidecar's asyncio loop."""
import asyncio
import lark_oapi.ws.client as sdk


class AsyncFeishuWsClient(sdk.Client):
    receive_task = None

    async def _connect(self):
        # This SDK captures a module loop at import. Each sidecar owns one client
        # under asyncio.run, so its event callbacks must use that running loop.
        sdk.loop = asyncio.get_running_loop()
        await super()._connect()
        await asyncio.sleep(0)

    async def _receive_message_loop(self):
        self.receive_task = asyncio.current_task()
        return await super()._receive_message_loop()
