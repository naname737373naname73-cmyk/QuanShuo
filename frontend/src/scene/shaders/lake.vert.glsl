#version 300 es
// 全屏三角形。vUv：左下角 (0,0)，右上角 (1,1)。
in vec2 aPosition;
out vec2 vUv;

void main() {
  vUv = aPosition * 0.5 + 0.5;
  gl_Position = vec4(aPosition, 0.0, 1.0);
}
