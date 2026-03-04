#include "shared/hash-functions.hlsl"
#include "shared/point.hlsl"
#include "shared/quat-functions.hlsl"
#include "shared/bias-functions.hlsl"

cbuffer FloatParams : register(b0)
{
    float3 Offset;
    float A;

    float B;
    float C;
    float D;
    float Time;
    float2 GainAndBias;
}

cbuffer IntParams : register(b1)
{
    uint TotalCount;
    int2 TexSize;
}

StructuredBuffer<Point> SourcePoints : t0;
Texture2D<float4> Image : register(t1);
Texture2D<float4> Gradient : register(t2);

RWStructuredBuffer<Point> ResultPoints : u0;
sampler Sampler : register(s0);
sampler ClampedSampler : register(s1);

//- DEFINES ------------------------------------
/*{defines}*/
//----------------------------------------------

float Biased(float f){return ApplyGainAndBias(f, GainAndBias);}
float4 SampleGradient(float f){return Gradient.SampleLevel(ClampedSampler, float2(f, 0.5), 0);}

[numthreads(64,1,1)]
void main(uint3 DTId : SV_DispatchThreadID)
{
    uint idx = DTId.x;

    uint numStructs, stride;
    SourcePoints.GetDimensions(numStructs, stride);
    if(idx >= TotalCount) {
        return;
    }

    float f = (float)idx / TotalCount;
    Point p = (Point)0;
    if(numStructs>0){
        p = SourcePoints[(idx)%numStructs];
    }else{
        p.Position=float3(0,0,0);
        p.FX1=1.0;
        p.Rotation=float4(0,0,0,1);
        p.Color=float4(1,1,1,1);
        p.Scale=float3(1,1,1);
        p.FX2=1.0;
   }

{
//- METHOD -------------------------------------
/*{method}*/
//----------------------------------------------
}
    ResultPoints[idx] = p;
}
