# Port of ModuleGridFinStepDeploy deploy math (lockMode = impact), 60 fps
import math, numpy as np, matplotlib; matplotlib.use("Agg"); import matplotlib.pyplot as plt
steps=[(45,3.0,3.0),(75,2.5,3.5),(90,2.5,0)]; r=1.5; reb=0.12; maxr=1.0; z=0.25; f=5.5; settle=1.1
shA,shF,shT=0.12,15,0.1
ease=lambda u:u*u*(3-2*u); iease=lambda u:(3-2*u)*u*u+r*(u**3-u*u)
def rebound(t,vin):
    w=2*math.pi*f; wd=w*math.sqrt(1-z*z); A=min(maxr,reb*vin)
    x=A*math.exp(-z*w*t)*abs(math.sin(wd*t))+shA*math.exp(-t/shT)*math.sin(2*math.pi*shF*t)
    fd=max(0,min(1,1-t/settle)); fd=fd*fd*(3-2*fd); return max(0,x*fd)
dt=1/60; T=[];A=[]; t=0; pos=0; vin=None
for i,(tg,mv,hd) in enumerate(steps):
    last=i==len(steps)-1; st=pos; pt=0
    while True:
        pt+=dt; u=min(1,pt/mv); pos=st+(tg-st)*(iease(u) if last else ease(u)); t+=dt; T.append(t);A.append(pos)
        if u>=1: break
    if last:
        vin=(tg-st)*r/mv; lt=pt-mv
        while lt<settle: lt+=dt; t+=dt; T.append(t); A.append(90-rebound(lt,vin))
    else:
        h=0
        while h<hd: h+=dt;t+=dt;T.append(t);A.append(pos)
T=np.array(T);A=np.array(A); V=np.gradient(A,T)
print("total %.2f s, impact %.1f deg/s, min after stop %.2f, max %.3f"%(T[-1],vin,A[T>T[-1]-settle].min(),A.max()))
fig,ax=plt.subplots(3,1,figsize=(11,10))
ax[0].plot(T,A);ax[0].set_ylabel("hinge angle (deg)");ax[0].set_title("GridFinStepDeploy deploy profile (60 fps, lockMode=impact)");ax[0].grid(alpha=.3)
ax[1].plot(T,V,color="tab:orange");ax[1].set_ylabel("angular rate (deg/s)");ax[1].grid(alpha=.3)
m=T>T[-1]-settle-1.0;ax[2].plot(T[m],A[m],".-",ms=3);ax[2].axhline(90,color="k",ls="--",lw=.8,label="hard stop");ax[2].set_ylabel("deg");ax[2].set_xlabel("time (s)");ax[2].set_title("final approach + hinge impact (zoom)");ax[2].legend();ax[2].grid(alpha=.3)
plt.tight_layout();plt.savefig("/workspace/gridfin_profile.png",dpi=110)
