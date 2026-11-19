import numpy as np, sys
a=np.fromfile(sys.argv[1],dtype=np.float32).reshape(-1,6);P=a[:,:3].astype(np.float64);C=a[:,3:]
r=np.linalg.norm(P,axis=1);d=P/r[:,None]
W,H=720,361
az=np.arctan2(d[:,0],-d[:,2]);el=np.arcsin(np.clip(d[:,1],-1,1))
cell=np.radians(0.5)
ix=np.floor((az+np.pi)/cell).astype(int).clip(0,W-1);iy=np.floor((np.pi/2-el)/cell).astype(int).clip(0,H-1)
R=np.full((H,W),np.nan);R[iy,ix]=r
ys=np.arange(H);xs=np.arange(W)
elv=np.pi/2-(ys+0.5)*cell;azv=(xs+0.5)*cell-np.pi
D=np.stack([np.cos(elv)[:,None]*np.sin(azv)[None,:],np.broadcast_to(np.sin(elv)[:,None],(H,W)),-np.cos(elv)[:,None]*np.cos(azv)[None,:]],-1)
Pg=D*R[...,None]
def sh(A,dy,dx): return np.roll(np.roll(A,dy,0),dx,1)
jump=float(sys.argv[2]) if len(sys.argv)>2 else 0.12
okm=~np.isnan(R)
Rl,Rr,Ru,Rd=sh(R,0,1),sh(R,0,-1),sh(R,1,0),sh(R,-1,0)
ok=okm&~np.isnan(Rl)&~np.isnan(Rr)&~np.isnan(Ru)&~np.isnan(Rd)
for Rn in (Rl,Rr,Ru,Rd): ok&=np.abs(Rn-R)<=jump*R
ok[0]=ok[-1]=False
N=np.cross(sh(Pg,0,-1)-sh(Pg,0,1),sh(Pg,1,0)-sh(Pg,-1,0))
nl=np.linalg.norm(N,axis=-1);ok&=nl>1e-9;N=N/np.maximum(nl[...,None],1e-12)
flip=(N*Pg).sum(-1)>0;N[flip]*=-1
idx=np.argwhere(ok);print("cells",int(okm.sum()),"with normals",len(idx))
pos=Pg[ok];nor=N[ok];rr=R[ok]
rng=np.random.default_rng(1)
band=float(sys.argv[3]) if len(sys.argv)>3 else 0.035;cos=float(sys.argv[4]) if len(sys.argv)>4 else 0.9
remaining=np.ones(len(pos),bool);planes=[]
for k in range(16):
    ridx=np.nonzero(remaining)[0]
    if len(ridx)<max(150,len(pos)//80): break
    sample=rng.choice(ridx,min(5000,len(ridx)),replace=False)
    best=(0,None,None)
    for c in rng.choice(ridx,300):
        n=nor[c];dd=-n@pos[c]
        s=((nor[sample]@n>=cos)&(np.abs(pos[sample]@n+dd)<band*rr[sample])).sum()
        if s>best[0]:best=(s,n,dd)
    if best[0]*len(ridx)/len(sample)<max(150,len(pos)//80): break
    n,dd=best[1],best[2]
    inl=remaining&(nor@n>=cos)&(np.abs(pos@n+dd)<2*band*rr)
    if inl.sum()<max(150,len(pos)//80): break
    n2=nor[inl].sum(0);n2/=np.linalg.norm(n2);d2=-(pos[inl]@n2).mean()
    planes.append((n2,d2,int(inl.sum())));remaining&=~inl
print("planes:",[(np.round(n,2).tolist(),round(float(dd),2),c) for n,dd,c in planes])
assigned=np.zeros(len(pos),bool)
for n,dd,c in planes:
    fac=D[ok]@n
    onp=-dd/np.where(fac<-0.15,fac,-1e9)
    m=~assigned&(nor@n>=cos)&(fac<-0.15)&(onp>rr*0.84)&(onp<rr*1.16)
    assigned|=m
print("assigned %.0f%% of cells with normals; normals are %.0f%% of observed cells"%(100*assigned.mean(),100*len(pos)/okm.sum()))

if len(sys.argv)>5:
    newr=R.copy()
    assigned=np.zeros(len(pos),bool);flat=rr.copy()
    for n,dd,c in planes:
        fac=D[ok]@n
        onp=-dd/np.where(fac<-0.15,fac,-1e9)
        m=~assigned&(nor@n>=cos)&(fac<-0.15)&(onp>rr*0.75)&(onp<rr*1.25)
        flat[m]=onp[m];assigned|=m
    newr[ok]=flat
    Pn=D*newr[...,None]
    Cg=np.zeros((H,W,3),np.float32);Cg[iy,ix]=C
    mask=~np.isnan(newr)
    out=np.concatenate([Pn[mask],Cg[mask]],1).astype(np.float32)
    out.tofile(sys.argv[5]);print("wrote",len(out))
