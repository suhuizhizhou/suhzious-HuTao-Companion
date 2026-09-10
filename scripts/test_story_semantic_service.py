"""Read-only smoke tests for a RUNNING loopback Story Dense service and its local index."""
import argparse
import http.client
import json
from pathlib import Path
from urllib.parse import urlsplit
import numpy as np

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument("--url",default="http://127.0.0.1:9891/")
    p.add_argument("--index",required=True)
    p.add_argument("--out",default=None)
    args=p.parse_args()
    url=urlsplit(args.url)
    if url.scheme!="http" or url.hostname not in ("127.0.0.1","localhost"):
        p.error("Only loopback HTTP is allowed")
    checks=[]
    def check(name,ok): checks.append({"name":name,"passed":bool(ok)})
    def request(method,path,value=None,headers=None):
        con=http.client.HTTPConnection(url.hostname,url.port or 80,timeout=5)
        body=json.dumps(value,ensure_ascii=False).encode() if value is not None else None
        try:
            con.request(method,path,body,headers or {"Content-Type":"application/json"})
            r=con.getresponse()
            return r.status,json.loads(r.read())
        finally:
            con.close()
    folder=Path(args.index)
    manifest=json.loads((folder/"manifest.json").read_text(encoding="utf-8"))
    ids=json.loads((folder/"ids.json").read_text(encoding="utf-8"))
    vectors=np.load(folder/"vectors.npy",mmap_mode="r",allow_pickle=False)
    check("dimensions_and_count",vectors.shape==(len(ids),manifest["dimension"]) and len(ids)==manifest["count"])
    check("unique_stable_ids",len(set(ids))==len(ids) and all(i.startswith(("textmap:","character:","archive:")) for i in ids))
    sample=vectors[::max(1,len(ids)//1000)]
    check("finite_embeddings",np.isfinite(sample).all())
    check("l2_normalized",np.allclose(np.linalg.norm(sample,axis=1),1,atol=1e-4))
    code,health=request("GET","/health")
    check("health_version",code==200 and health.get("corpus_version")==manifest["corpus_version"])
    query={"query":"胡桃的宠物石狮子叫什么","corpus_version":manifest["corpus_version"],"top_k":5}
    code,hits=request("POST","/search",query)
    check("real_model_topk",code==200 and len(hits)==5 and all(h["id"] in ids for h in hits))
    check("cosine_descending",code==200 and all(-1<=h["score"]<=1 for h in hits)
          and all(a["score"]>=b["score"] for a,b in zip(hits,hits[1:])))
    code,_=request("POST","/search",{**query,"corpus_version":"old-version"})
    check("stale_index_409",code==409)
    code,_=request("POST","/search",query,{"Origin":"https://example.com","Content-Type":"application/json"})
    check("browser_origin_403",code==403)
    code,_=request("POST","/search",query,{"Host":"attacker.example","Content-Type":"application/json"})
    check("unexpected_host_403",code==403)
    code,_=request("POST","/search",{**query,"top_k":101})
    check("bounded_topk",code==400)
    code,_=request("POST","/search",{**query,"query":""})
    check("empty_query_rejected",code==400)
    code,_=request("POST","/search",{**query,"query":"桃"*4001})
    check("long_query_rejected",code==400)
    code,_=request("POST","/search",{"query":"x"*21000})
    check("oversize_body_413",code==413)
    code,_=request("GET","/unknown")
    check("unknown_path_404",code==404)
    report={"model":manifest["model"],"revision":manifest["revision"],"count":len(ids),
            "passed":sum(c["passed"] for c in checks),"total":len(checks),"checks":checks}
    if args.out:
        target=Path(args.out)
        target.parent.mkdir(parents=True,exist_ok=True)
        target.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding="utf-8")
    print(json.dumps(report,ensure_ascii=False,indent=2))
    return 0 if all(c["passed"] for c in checks) else 1

if __name__=="__main__":
    raise SystemExit(main())
