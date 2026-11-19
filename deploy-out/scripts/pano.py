import numpy as np, sys
from PIL import Image
src,out=sys.argv[1],sys.argv[2]
a=np.fromfile(src,dtype=np.float32).reshape(-1,6)
p=a[:,:3];c=a[:,3:]
r=np.linalg.norm(p,axis=1);print("n",len(r),"range pct",np.percentile(r,[1,25,50,75,99]))
az=np.arctan2(p[:,0],-p[:,2]);el=np.arcsin(np.clip(p[:,1]/r,-1,1))
print("el deg",np.degrees(el.min()),np.degrees(el.max()))
W,H=1440,720
ix=((az+np.pi)/(2*np.pi)*W).astype(int).clip(0,W-1);iy=((np.pi/2-el)/np.pi*H).astype(int).clip(0,H-1)
img=np.zeros((H,W,3),np.uint8);img[iy,ix]=(c*255).clip(0,255).astype(np.uint8)
d=np.zeros((H,W),np.float32);d[iy,ix]=r
lo,hi=np.percentile(r,[2,98]);n=np.clip((d-lo)/(hi-lo),0,1)
rgb=np.zeros((H,W,3),np.uint8);rgb[...,0]=n*255;rgb[...,2]=255-n*255;rgb[...,1]=np.sin(n*np.pi)*180;rgb[d==0]=0
Image.fromarray(np.vstack([img[:360],rgb[:360]])).save(out)
