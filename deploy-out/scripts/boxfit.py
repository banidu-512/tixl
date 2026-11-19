import numpy as np, sys
from PIL import Image
a=np.fromfile(sys.argv[1],dtype=np.float32).reshape(-1,6)
p=a[:,:3].astype(np.float64);col=a[:,3:]
r=np.linalg.norm(p,axis=1);d=p/r[:,None]
W,H=1440,720
az=np.arctan2(d[:,0],-d[:,2]);el=np.arcsin(np.clip(d[:,1],-1,1))
ix=((az+np.pi)/(2*np.pi)*W).astype(int).clip(0,W-1);iy=((np.pi/2-el)/np.pi*H).astype(int).clip(0,H-1)
R=np.full((H,W),np.nan);C=np.zeros((H,W,3));R[iy,ix]=r;C[iy,ix]=col
valid=~np.isnan(R)
ys,xs=np.nonzero(valid)
elv=np.pi/2-(ys+0.5)/H*np.pi;azv=(xs+0.5)/W*2*np.pi-np.pi
D=np.stack([np.cos(elv)*np.sin(azv),np.sin(elv),-np.cos(elv)*np.cos(azv)],1)
robs=R[ys,xs]
rng=np.random.default_rng(0);sel=rng.choice(len(robs),min(40000,len(robs)),replace=False)
Ds,rs=D[sel],robs[sel]
def box_range(D,prm):
    psi,xp,xn,zp,zn,yp,yn=prm
    c,s=np.cos(-psi),np.sin(-psi)
    x=c*D[:,0]+s*D[:,2];z=-s*D[:,0]+c*D[:,2];y=D[:,1]
    big=1e9
    def t(comp,pos,neg):
        return np.where(comp>1e-9,pos/np.maximum(comp,1e-9),np.where(comp<-1e-9,neg/np.maximum(-comp,1e-9),big))
    return np.minimum(np.minimum(t(x,xp,xn),t(z,zp,zn)),t(y,yp,yn))
def cost(prm):
    if min(prm[1:])<=0.05: return 1e9
    rb=box_range(Ds,prm);e=np.abs(np.log(rs/rb))
    return np.mean(np.minimum(e,0.3))   # truncated: furniture and noise cannot dominate
best=None
med=np.median(rs)
for psi0 in np.linspace(0,np.pi/2,10,endpoint=False):
    for k in range(4):
        x0=np.array([psi0]+list(med*rng.uniform(0.6,1.6,6)))
        from scipy.optimize import minimize
        res=minimize(cost,x0,method='Nelder-Mead',options={'maxiter':1500,'xatol':1e-4,'fatol':1e-6})
        if best is None or res.fun<best.fun: best=res
prm=best.x;prm[0]=prm[0]%(np.pi/2)
print("cost",best.fun,"yaw deg",np.degrees(prm[0]),"x+/-",prm[1],prm[2],"z+/-",prm[3],prm[4],"y ceil/floor",prm[5],prm[6])
rb=box_range(D,prm);e=np.abs(np.log(robs/rb))
print("within 15% of box:",np.mean(e<0.15),"within 30%:",np.mean(e<0.3))
np.save(sys.argv[2],prm)
# visual: box range pano vs observed
def cmap(rr,lo,hi):
    n=np.clip((rr-lo)/(hi-lo),0,1);o=np.zeros(rr.shape+(3,),np.uint8);o[...,0]=n*255;o[...,2]=255-n*255;o[...,1]=np.sin(n*np.pi)*180;return o
lo,hi=np.percentile(robs,[2,98])
Ob=np.zeros((H,W,3),np.uint8);Ob[ys,xs]=cmap(robs,lo,hi)
Bx=np.zeros((H,W,3),np.uint8);Bx[ys,xs]=cmap(rb,lo,hi)
Image.fromarray(np.vstack([Ob[:360],Bx[:360]])).save(sys.argv[3])
