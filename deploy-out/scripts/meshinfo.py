import numpy as np, sys
f=open(sys.argv[1],'rb'); hdr=b''
while not hdr.endswith(b'end_header\n'): hdr+=f.readline()
print(hdr.decode().replace('\n',' | '))
nv=int([l for l in hdr.decode().split('\n') if l.startswith('element vertex')][0].split()[-1])
nf=int([l for l in hdr.decode().split('\n') if l.startswith('element face')][0].split()[-1])
v=np.frombuffer(f.read(nv*15),dtype=np.dtype([('p','<f4',3),('c','u1',3)]))
fa=np.frombuffer(f.read(nf*13),dtype=np.dtype([('n','u1'),('i','<i4',3)]))
idx=fa['i']
e=np.concatenate([idx[:,[0,1]],idx[:,[1,2]],idx[:,[2,0]]]);e.sort(axis=1)
k=e[:,0].astype(np.int64)*nv+e[:,1]
u,c=np.unique(k,return_counts=True)
print("verts",nv,"faces",nf,"edges",len(u),"boundary",int((c==1).sum()),"nonmanifold",int((c>2).sum()))
r=np.linalg.norm(v['p'],axis=1);print("range pct",np.percentile(r,[1,50,99]))
