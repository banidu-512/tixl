#!/bin/bash
# usage: runscan.sh <out.f32> ; launches editor, runs one full sweep, dumps points
B='C:\Users\Artonurban\.claude\jobs\fab667d6\tmp\bridge.py'; SP=765eb3fe-47e9-481f-b685-335af68a55be; DS=3d5ca7f1-8b24-4c6a-9e35-2f1d0a9b7c44
cd /d/CrossTixlClaude/Editor/bin/Release/net10.0 && (nohup ./TiXL.exe --debug-server 9042 >/dev/null 2>&1 &)
for i in $(seq 1 45); do python -I "$B" 9042 ping >/dev/null 2>&1 && break; sleep 2; done; sleep 15
python -I "$B" 9042 openProject '{"name":"Test"}' >/dev/null; sleep 25
python -I "$B" 9042 setInput '{"childId":"'$SP'","inputName":"Run","value":false}' >/dev/null
python -I "$B" 9042 setInput '{"childId":"'$SP'","inputName":"SweepSpeed","value":10}' >/dev/null
python -I "$B" 9042 setInput '{"childId":"'$SP'","inputName":"Continuous","value":false}' >/dev/null
python -I "$B" 9042 setInput '{"childId":"'$DS'","inputName":"PixelStride","value":1}' >/dev/null
python -I "$B" 9042 setInput '{"childId":"7e9b2d04-6c15-4a8f-b3e7-5c8d1f2a6e93","inputName":"PointSize","value":0.012}' >/dev/null
python -I "$B" 9042 pin '{"childId":"7e9b2d04-6c15-4a8f-b3e7-5c8d1f2a6e93"}' >/dev/null
python -I "$B" 9042 setInput '{"childId":"'$DS'","inputName":"Reset","value":true}' >/dev/null; sleep 1
python -I "$B" 9042 setInput '{"childId":"'$DS'","inputName":"Reset","value":false}' >/dev/null
python -I "$B" 9042 setInput '{"childId":"'$SP'","inputName":"Restart","value":true}' >/dev/null; sleep 1
python -I "$B" 9042 setInput '{"childId":"'$SP'","inputName":"Restart","value":false}' >/dev/null
python -I "$B" 9042 setInput '{"childId":"'$SP'","inputName":"Run","value":true}' >/dev/null
for i in $(seq 1 120); do sleep 6; python -I "$B" 9042 getOutput '{"childId":"'$SP'","outputName":"Done","update":true}' | grep -q '"value": true' && break; done
python -I "$B" 9042 getOutput '{"childId":"'$DS'","outputName":"PointCount","update":true}' | grep -o '"value": [0-9]*'
python -I /d/CrossTixlClaude/deploy-out/scripts/dump.py "$1"
