"""Legacy bridge to the authoritative C# financial service (no Python financial provider/cache).
Default: offline even if expired. Missing => explicit pending, never akshare/Tushare fallback.
Set THREEBUCKET_FINANCIAL_CLI only to select a built local CLI executable/DLL, not a credential.
"""
from pathlib import Path
import os, json, subprocess, sys

def _cli():
    configured = os.environ.get("THREEBUCKET_FINANCIAL_CLI")
    if configured:
        path = Path(configured)
        if not path.is_file():
            raise RuntimeError("Shared financial CLI missing: " + str(path))
        return ["dotnet", str(path)] if path.suffix.lower() == ".dll" else [str(path)]
    root = Path(__file__).resolve().parents[2]
    candidates = list((root / "desktop/windows/dist").glob("ThreeBucket-win-x64-v*/cli/ThreeBucket.Cli.exe"))
    def version(p):
        try: return tuple(int(x) for x in p.parents[1].name.rsplit("v", 1)[1].split("."))
        except ValueError: return (0,)
    if candidates:
        return [str(max(candidates, key=version))]
    raise RuntimeError("No C# shared-financial CLI. Build/package v1.5.6+ or set THREEBUCKET_FINANCIAL_CLI. Direct financial network fallback is disabled.")

def shared_yjbb_raw(period):
    import pandas as pd
    root = Path(__file__).resolve().parents[2]
    data = Path(os.environ.get("THREEBUCKET_FINANCIAL_DATA", str(root / "data")))
    proc = subprocess.run(_cli() + ["--financial-cache", "yjbb", "--period", str(period), "--data", str(data)], capture_output=True, text=True, encoding="utf-8", timeout=120)
    if proc.returncode:
        raise RuntimeError("C# financial adapter failed (no direct fallback): " + proc.stderr.strip())
    response = json.loads(proc.stdout)
    if response.get("Protocol") != "shared-financial-cli-v1" or response.get("Provider") != "eastmoney" or response.get("NetworkCalls") != 0:
        raise RuntimeError("Invalid/offline-violating shared-financial protocol")
    if response.get("Status") == "missing/pending":
        print("[shared financial] " + str(period) + " missing/pending; use explicit C# supplement-missing/financial refresh", file=sys.stderr)
    mapping = {"Code":"股票代码", "Name":"股票简称", "Industry":"所处行业", "Revenue":"营业总收入-营业总收入", "RevYoy":"营业总收入-同比增长", "Np":"净利润-净利润", "NpYoy":"净利润-同比增长", "Roe":"净资产收益率", "OcfPs":"每股经营现金流量", "Eps":"每股收益", "GrossMargin":"销售毛利率"}
    rows = [{v: row.get(k) for k,v in mapping.items()} for row in response.get("Rows", [])]
    out = pd.DataFrame(rows, columns=list(mapping.values()))
    out.attrs["financial_source"] = response
    return out


def validated_config(path):
    proc = subprocess.run(_cli() + ["--config", "export", "--family", "candidate", "--path", str(path)], capture_output=True, text=True, encoding="utf-8", timeout=120)
    if proc.returncode:
        raise ValueError("C# configuration validation failed: " + proc.stderr.strip())
    response = json.loads(proc.stdout)
    if response.get("Protocol") != "validated-config-v1":
        raise ValueError("Invalid C# config protocol")
    return response


class SharedTushareProxy:
    """Only supplemental SDK stays Python; C# owns policy, raw store and locking."""
    def __init__(self, root, refresh=False):
        self.root, self.refresh = Path(root), refresh

    def __getattr__(self, resource):
        def call(**params):
            import pandas as pd
            command = _cli() + ["--financial-cache", "tushare", "--root", str(self.root), "--resource", resource, "--params-json", json.dumps(params, ensure_ascii=False, sort_keys=True), "--python", sys.executable]
            if self.refresh:
                command.append("--refresh")
            proc = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", timeout=240)
            if proc.returncode:
                raise RuntimeError("C# supplemental service failed: " + proc.stderr.strip())
            result = json.loads(proc.stdout)
            if result.get("Protocol") != "shared-financial-cli-v1" or result.get("Provider") != "tushare":
                raise RuntimeError("Invalid supplemental protocol")
            if result.get("Failures"):
                print("[shared Tushare] refresh failed; retained/missing data explicitly reported", file=sys.stderr)
            return pd.DataFrame(result.get("Rows", []))
        return call
