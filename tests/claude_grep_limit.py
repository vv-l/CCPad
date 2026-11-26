"""Exercise the actual Git Bash entry points in an isolated CC Pad cache on Windows."""
import json
import os
from pathlib import Path
import subprocess
import tempfile

assets = Path(__file__).resolve().parents[1] / "CCPad/Assets/claude-shell"


with tempfile.TemporaryDirectory(prefix="ccpad-grep-test-") as temp:
    work = Path(temp)
    setup = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
             str(assets / "prepare.ps1"), "-CacheRoot", str(work / "cache")]
    bash = Path(subprocess.check_output(setup, encoding="utf-8").strip())
    assert bash.is_file() and bash.is_relative_to(work), "Expected an isolated Git Bash"
    assert Path(subprocess.check_output(setup, encoding="utf-8").strip()) == bash
    env = dict(os.environ, PYTHONUTF8="1", TEST_WORK=work.as_posix())

    def run(command, data=b""):
        return subprocess.run([bash, "-lc", command], input=data, capture_output=True,
                              timeout=40, env=env, cwd=work)

    for name in ("grep", "grep.exe", "command /usr/bin/grep", "/bin/grep.exe",
                 "env /usr/bin/grep", "sh -c '/bin/grep -F yes' --"):
        result = run(name + " -F yes", b"yes\nno\n")
        assert (result.returncode, result.stdout) == (0, b"yes\n"), (name, result)
    assert run("grep -F missing", b"line\n").returncode == 1
    assert run("grep '['", b"line\n").returncode == 2
    assert run("grep -F yes | grep -v no", b"yes\nno\n").stdout == b"yes\n"

    log = work / "test log.jsonl"
    with log.open("w", encoding="utf-8") as f:
        for command in ["needle one", "other", 'needle "two"', "needle 三"]:
            row = {"type": "assistant", "message": {"content": [
                {"type": "tool_use", "name": "Bash", "input": {"command": command}}]}}
            f.write(json.dumps(row) + "\n")
        f.write('{"incomplete"')
    result = run('ccpad-log-commands "$TEST_WORK/test log.jsonl" needle --last 2')
    assert result.returncode == 0 and result.stdout.decode().splitlines() == ['needle "two"', "needle 三"], result

    # Query the Windows job inside the real launch path without allocating 1 GiB.
    real_grep = bash.parent / "ccpad-gnu-grep.exe"
    original = real_grep.with_suffix(".original")
    real_grep.rename(original)
    real_grep.write_bytes(b'#!/usr/bin/bash\nexec python "$@"\n')
    (work / "query.py").write_text(
        "import win32job\n"
        "i=win32job.QueryInformationJobObject(None, win32job.JobObjectExtendedLimitInformation)\n"
        "print(i['BasicLimitInformation']['LimitFlags'] & win32job.JOB_OBJECT_LIMIT_PROCESS_MEMORY, i['ProcessMemoryLimit'])\n",
        encoding="ascii")
    for entry in ("grep", "/usr/bin/grep", "env /bin/grep.exe"):
        result = run(entry + ' "$TEST_WORK/query.py"')
        assert result.returncode == 0 and result.stdout.split() == [b"256", b"1073741824"], result
    real_grep.unlink()
    original.rename(real_grep)

    # Use a finite NUL-free record and a reduced fixture ceiling. No pathological regex.
    sample = work / "record"
    with sample.open("wb") as f:
        for _ in range(80):
            f.write(b"x" * (1024 * 1024))
    assert run('grep -zF absent "$TEST_WORK/record"').returncode == 1
    for entry in ("grep", "grep.exe"):
        path = bash.parent / entry
        path.write_bytes(path.read_bytes().replace(b"1048576", b"65536"))
    result = run('set -o pipefail; /usr/bin/grep.exe -zF absent "$TEST_WORK/record" | cat; '
                 'rc=$?; printf "grep=%d\\n" "$rc"; printf "parent-ok\\n"')
    assert result.returncode == 0 and result.stdout.splitlines() == [b"grep=2", b"parent-ok"], result
    assert result.stderr, "Expected GNU grep to report allocation failure"
    print("PASS normal, absolute, nested and pipeline calls; JSONL lookup; Windows 1 GiB job; low-cap failure; shell survived")
