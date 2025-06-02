#include "lib/shared/point.hlsl"
#include "lib/shared/quat-functions.hlsl"
cbuffer Params : register(b0)
{
    int startIndex;    // 0 3 6
}
RWStructuredBuffer<LegacyPoint> PointsBuffer : t0;
StructuredBuffer<LegacyPoint> Points : t1;            // input
RWStructuredBuffer<LegacyPoint> ResultPoints : u0;    // output

[numthreads(256,1,1)]
void main(uint3 i : SV_DispatchThreadID)
{
    uint sizea, size, stride;
    PointsBuffer.GetDimensions(sizea, stride);
    Points.GetDimensions(size, stride);

    if(i.x > size)  // 9
        return;
    
    if (startIndex == 0) {
        PointsBuffer[i.x] = Points[i.x];
    }

    ResultPoints[i.x].Rotation = Points[i.x].Rotation;
    ResultPoints[i.x].Position = Points[i.x].Position;
    ResultPoints[i.x].W = max(Points[i.x].Color ,PointsBuffer[i.x].Color);
    ResultPoints[i.x].Stretch = Points[i.x].Stretch;
    ResultPoints[i.x].Selected = Points[i.x].Selected;
    ResultPoints[i.x].Color = max(Points[i.x].Color ,PointsBuffer[i.x].Color);
    PointsBuffer[i.x] = ResultPoints[i.x];

}
