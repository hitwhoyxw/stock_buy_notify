"""Tushare SDK worker. Invoked only under C# FinancialRawStore lock; never owns a cache."""
import sys, json
from pathlib import Path

def main():
    import tushare as ts
    from fetch_supplement import load_token
    resource, root = sys.argv[1], Path(sys.argv[2])
    params = json.loads(sys.stdin.read())
    token, _ = load_token(root)
    if not token:
        raise RuntimeError("Tushare credentials unavailable")
    pro = ts.pro_api(token)
    fn = getattr(pro, resource, None)
    if fn is None:
        raise RuntimeError("Unsupported Tushare API")
    frame = fn(**params)
    records = [] if frame is None else json.loads(frame.to_json(orient="records", date_format="iso", force_ascii=False))
    print(json.dumps({"result":{"data":records}}, ensure_ascii=False, allow_nan=False))

if __name__ == "__main__":
    try:
        main()
    except Exception:
        print("Supplemental provider failed; no credential values logged", file=sys.stderr)
        sys.exit(2)
