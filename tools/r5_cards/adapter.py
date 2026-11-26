"""Exact task-bound public text for the pinned, original shared card."""
import hashlib
import importlib.util
import os
from pathlib import Path
import re
import threading
from types import SimpleNamespace

PINS = {
    "cardkit_streamer.py": "365e6af5fc0bff634a84ef704ac6df81213a4b9a697943e7550c7adc9d5e96e2",
    "token_usage.py": "69aa5a1f400704a35135808664d31b93a73b3e5ea9c6f3e27dc8bf4586c92879",
}
IDENTITY = ("app_id", "chat_id", "message_id", "task_id", "input_id",
            "window_id", "pane_id", "generation", "thread_id", "turn_id")


def _reporter_class():
    vendor = Path(__file__).parent / "vendor"
    for name, expected in PINS.items():
        if hashlib.sha256((vendor / name).read_bytes()).hexdigest() != expected:
            raise RuntimeError("old card source pin mismatch")
    spec = importlib.util.spec_from_file_location("r5_pinned_cardkit", vendor / "cardkit_streamer.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.CardKitReporter


def _reject_fallback(*args, **kwargs):
    raise RuntimeError("old card creation failed; entry remains blocked")


class _CheckedClient:
    def __init__(self, client):
        self.failed = threading.Event()
        self._error_class = ""
        self._lock = threading.RLock()
        wrap = self._wrap
        self.im = SimpleNamespace(v1=SimpleNamespace(message=SimpleNamespace(
            create=wrap(client.im.v1.message.create), reply=wrap(client.im.v1.message.reply))))
        self.cardkit = SimpleNamespace(v1=SimpleNamespace(
            card=SimpleNamespace(create=wrap(client.cardkit.v1.card.create),
                                 settings=wrap(client.cardkit.v1.card.settings)),
            card_element=SimpleNamespace(content=wrap(client.cardkit.v1.card_element.content),
                                         update=wrap(client.cardkit.v1.card_element.update))))

    def _wrap(self, method):
        def checked(request):
            with self._lock:
                self.check()
                try:
                    response = method(request)
                    if not response.success():
                        self._error_class = "ApiFailure"
                        self.failed.set()
                    else:
                        return response
                except Exception as exc:
                    self._error_class = type(exc).__name__
                    self.failed.set()
                raise RuntimeError("card transport failed: " + self._error_class) from None
        return checked

    def check(self):
        if self.failed.is_set():
            raise RuntimeError("card transport blocked: " + self._error_class) from None


class TaskCards:
    def __init__(self, client, *, can_abort=None, task_action=None):
        self.client = _CheckedClient(client)
        self.can_abort = can_abort
        self.task_action = task_action
        self._reporter = _reporter_class()
        self._tasks = {}
        self._lock = threading.RLock()
        self._closed = False

    def create(self, task):
        binding = {key: task[key] for key in IDENTITY}
        if any(value is None or value == "" for value in binding.values()):
            raise ValueError("missing task binding")
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", str(binding["task_id"])):
            raise ValueError("invalid task id")
        with self._lock:
            self._check_transport()
            if self._closed:
                raise RuntimeError("cards closed")
            existing = self._tasks.get(binding["task_id"])
            if existing:
                if existing["binding"] != binding:
                    raise ValueError("task identity conflict")
                return existing["reporter"]
            for state in self._tasks.values():
                previous = state["binding"]
                if (previous["app_id"] == binding["app_id"]
                        and previous["input_id"] == binding["input_id"]):
                    raise ValueError("input already owns a task card")
            if not os.environ.get("AUTOBOT_STATE_DIR"):
                raise RuntimeError("private card state directory required")
            if not callable(self.task_action) or not callable(self.can_abort) or not self.can_abort(dict(binding)):
                raise RuntimeError("exact abort capability required")
            reporter = self._reporter(
                self.client, binding["chat_id"], title="Codex 工作中", task_id=binding["task_id"],
                reply_to=binding["message_id"],
                reply_in_thread=bool(task.get("reply_in_thread", False)),
                legacy_factory=_reject_fallback,
            )
            try:
                reporter.start()
                self.client.check()
                if not reporter.streamer.card_id or not reporter.streamer.message_id:
                    raise RuntimeError("old card creation incomplete")
            except BaseException:
                reporter._heartbeat_stop.set()
                if reporter._heartbeat_thread:
                    reporter._heartbeat_thread.join(timeout=1)
                raise
            self._tasks[binding["task_id"]] = {
                "binding": binding, "reporter": reporter, "seq": -1,
                "terminal": False, "final_text": "", "abort_requested": False,
            }
            return reporter

    def emit(self, envelope):
        with self._lock:
            self._check_transport()
            state = self._tasks.get(envelope.get("task_id"))
            if self._closed or not state or state["terminal"]:
                return False
            if any(envelope.get(key) != value for key, value in state["binding"].items()):
                return False
            seq = envelope.get("seq")
            if type(seq) is not int or seq <= state["seq"]:
                return False
            kind, payload = envelope.get("type"), envelope.get("payload")
            if not isinstance(payload, dict):
                return False
            reporter = state["reporter"]
            if kind in ("process_message", "partial_text"):
                phase, text = payload.get("phase"), payload.get("text")
                if phase not in ("commentary", "final_answer") or not isinstance(text, str):
                    return False
                if (kind == "process_message") != (phase == "commentary"):
                    return False
                # Never forward arbitrary runner payload, tools or private reasoning.
                event = {"type": kind, "text": text, "completed": True,
                         "item_id": str(seq)}
                reporter.on_event(event)
                self._check_transport()
                if phase == "final_answer":
                    state["final_text"] = text
            elif kind == "terminal":
                status = payload.get("status")
                if status not in ("success", "cancelled"):
                    return False
                reporter.finalize(state["final_text"], status=status)
                self._check_transport()
                state["terminal"] = True
            else:
                return False
            state["seq"] = seq
            return True

    def matches_card(self, task_id, chat_id, card_message_id):
        with self._lock:
            state = self._tasks.get(task_id)
            return bool(not self._closed and not self.client.failed.is_set() and state and not state["terminal"]
                        and state["binding"]["chat_id"] == chat_id
                        and state["reporter"].streamer.message_id == card_message_id)

    def accepts_abort(self, task_id, chat_id, card_message_id):
        with self._lock:
            state = self._tasks.get(task_id)
            return bool(self.matches_card(task_id, chat_id, card_message_id)
                        and not state["abort_requested"]
                        and callable(self.can_abort)
                        and self.can_abort(dict(state["binding"])))

    def abort(self, task_id, chat_id, card_message_id):
        with self._lock:
            if not self.accepts_abort(task_id, chat_id, card_message_id):
                return False
            # Transport ACK is not a terminal event: the source must confirm cancellation.
            state = self._tasks[task_id]
            accepted = bool(self.task_action("abort", {**state["binding"],
                                                       "card_message_id": card_message_id}))
            if accepted:
                state["abort_requested"] = True
            return accepted

    def _check_transport(self):
        if self.client.failed.is_set():
            for state in self._tasks.values():
                state["reporter"]._heartbeat_stop.set()
        self.client.check()

    def close(self):
        with self._lock:
            self._closed = True
            reporters = [state["reporter"] for state in self._tasks.values()]
            for reporter in reporters:
                reporter._heartbeat_stop.set()
        for reporter in reporters:
            thread = reporter._heartbeat_thread
            if thread and thread is not threading.current_thread():
                thread.join(timeout=1)
