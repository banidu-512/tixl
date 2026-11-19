import numpy as np, sys
a=np.fromfile(sys.argv[1],dtype=np.float32).reshape(-1,6);p=a[:,:3].astype(np.float64)
r=np.linalg.norm(p,axis=1);d=p/r[:,None]
W,H=1440,720
az=np.arctan2(d[:,0],-d[:,2]);el=np.arcsin(np.clip(d[:,1],-1,1))
ix=((az+np.pi)/(2*np.pi)*W).astype(int).clip(0,W-1);iy=((np.pi/2-el)/np.pi*H).astype(int).clip(0,H-1)
L=np.full((H,W),np.nan);L[iy,ix]=np.log(r)
# the shell is a 0.5 deg grid: compare cells two apart
g=[]
for dy,dx in ((0,2),(2,0)):
    A=L[:H-dy,:W-dx];B=L[dy:,dx:];m=~np.isnan(A)&~np.isnan(B);g.append(np.abs(A[m]-B[m]))
g=np.concatenate(g)
print("%s: n=%d  median |dlogr| %.4f  >5%%: %.1f%%  >15%%: %.1f%%"%(sys.argv[1].split("/")[-1],len(r),np.median(g),100*np.mean(g>0.05),100*np.mean(g>0.15)))
