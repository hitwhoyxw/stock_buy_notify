"""Real legacy-bridge compatibility tests; local fixtures only, no provider/credential reads."""
from pathlib import Path
import sys, os, json, shutil, hashlib, subprocess, socket
root, work, cli, harness = map(lambda x: Path(x).resolve(), sys.argv[1:5])
work.mkdir(parents=True, exist_ok=True)
os.environ["THREEBUCKET_FINANCIAL_CLI"] = str(cli)
os.environ["THREEBUCKET_FINANCIAL_DATA"] = str(work / "data")
sys.path.insert(0, str(root / "scripts"))
# Reject any attempted Python-side network (C# child additionally has strict offline guards).
def denied(*args, **kwargs):
    raise AssertionError("NETWORK_FORBIDDEN_IN_LEGACY_TEST")
socket.socket.connect = denied
from lib.shared_financial import shared_yjbb_raw, validated_config, SharedTushareProxy
import pandas as pd
passed, failed, tests = 0, 0, []
def check(ok, name):
    global passed, failed
    tests.append({"Name":name,"Passed":bool(ok)})
    if ok: passed += 1
    else: failed += 1; print("FAIL", name)
def file_hash(p): return hashlib.sha256(p.read_bytes()).hexdigest()
cache = work / "data/cache"; cache.mkdir(parents=True, exist_ok=True)
source = root / "data/cache/yjbb_20260930.json"
if not source.exists():
    raise RuntimeError("Real retained YJBB fixture missing; test must not fabricate a replacement")
target = cache / source.name; shutil.copy2(source, target); os.utime(target, (946656000,946656000))
before = file_hash(target)
raw = json.loads(source.read_text(encoding="utf-8-sig"))
frame = shared_yjbb_raw("20260930")
check(len(frame)==len(raw), "actual legacy/C# retained row count parity")
mapfields={"Revenue":"营业总收入-营业总收入","RevYoy":"营业总收入-同比增长","Np":"净利润-净利润","NpYoy":"净利润-同比增长","Roe":"净资产收益率","OcfPs":"每股经营现金流量","Eps":"每股收益","GrossMargin":"销售毛利率"}
idx=frame.set_index("股票代码")
for row in raw[:25]:
    code=str(row["Code"]).zfill(6)
    for field,column in mapfields.items():
        old, new = row.get(field), idx.loc[code,column]
        check(pd.isna(new) if old is None else float(old)==float(new), "actual retained unit/value parity "+code+" "+field)
check(before==file_hash(target), "legacy offline adapter preserves expired typed cache bytes")
check(len(shared_yjbb_raw("20991231"))==0,"missing legacy report explicit pending no network fallback")
config_path = root / "trading-system/02_strategy_config.yaml"; before_config=file_hash(config_path)
cfg=validated_config(config_path)
check(cfg["Family"]=="candidate" and len(cfg["Hash"])==64,"legacy config uses authoritative schema/hash")
check(before_config==file_hash(config_path),"legacy config export pure")
invalid=work/"bad_candidate.yaml";invalid.write_text(config_path.read_text(encoding="utf-8-sig")+"\nunknown_typo: 1\n",encoding="utf-8")
try:validated_config(invalid);check(False,"legacy malformed config rejected")
except ValueError as ex:check("unknown_typo" in str(ex),"legacy malformed config precise key")
proxy=SharedTushareProxy(work,False)
check(proxy.fina_audit(ts_code="001872.SZ").empty,"Tushare offline missing never launches credential/provider worker")
# Actual explicit supplemental orchestrator offline path (outputs only into WORK).
proc=subprocess.run([sys.executable,str(root/"scripts/tushare_supplement/fetch_supplement.py"),"--root",str(work),"--dry-run","--offline"],capture_output=True,text=True,encoding="utf-8",env=os.environ.copy())
check(proc.returncode==0,"supplement orchestrator dry-run offline no credentials/SDK required")
if proc.returncode: print(proc.stderr)
# Python launches C# fake-provider workers, sharing the exact C# store/lock with a direct C# invocation.
fake=work/"python-cross-process-store";fake.mkdir(exist_ok=True)
children=[subprocess.Popen(["dotnet",str(harness),"--store-worker",str(fake)]) for _ in range(6)]
for child in children:check(child.wait()==0,"Python-to-C# fake shared-store worker success")
check(len((fake/"fake_fetches.txt").read_text().splitlines())==1,"Python-launched C# workers one fake provider acquisition")
check(not list(cache.glob("*.meta")),"offline adapter creates no TTL sidecar")
(work/"legacy_results.json").write_text(json.dumps({"Passed":passed,"Failed":failed,"Tests":tests},ensure_ascii=False,indent=2),encoding="utf-8")
print(json.dumps({"Passed":passed,"Failed":failed,"Report":str(work/"legacy_results.json")},ensure_ascii=False))
sys.exit(0 if failed==0 else 1)
