#!/bin/bash
# usage: runroom.sh <out.f32> ; RoomScan3D (encoder pose) full sweep then dump points
B='C:\Users\Artonurban\.claude\jobs\fab667d6\tmp\bridge.py'; PATHID=03f30c91-ed6c-5ae8-888f-8aa1e2c53b33; DRAW=75a12238-2c6d-5e5f-9556-1ea5d0bd0420; SCAN=0045480b-06c7-550e-ab82-6db21d5a6083
code=$(curl -s -m 5 -o /dev/null -w "%{http_code}" "http://10.0.0.111/axis-cgi/com/ptz.cgi?query=position"); [ "$code" = "200" ] || [ "$code" = "401" ] || { echo "camera not answering (http $code), not starting"; exit 2; }
cd /d/CrossTixlClaude/Editor/bin/Release/net10.0 && (nohup ./TiXL.exe --debug-server 9042 >/dev/null 2>&1 &)
for i in $(seq 1 45); do python -I "$B" 9042 ping >/dev/null 2>&1 && break; sleep 2; done; sleep 15
python -I "$B" 9042 openProject '{"name":"RoomScan3D"}' >/dev/null; sleep 25
python -I "$B" 9042 pin '{"childId":"'$DRAW'"}' >/dev/null
for i in $(seq 1 120); do sleep 6; python -I "$B" 9042 getOutput '{"childId":"'$PATHID'","outputName":"Done","update":true}' | grep -q '"value": true' && break; done
python -I "$B" 9042 getOutput '{"childId":"'$SCAN'","outputName":"PointCount","update":true}' | grep -o '"value": [0-9]*'
sleep 12; python -I /d/CrossTixlClaude/deploy-out/scripts/dump.py "$1"
