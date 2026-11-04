export class SceneClock {
  private fixedTime: number;
  private readonly startedAt = performance.now() / 1000;

  constructor(initialTime: number, private fixed: boolean) {
    this.fixedTime = initialTime;
  }

  get isFixed(): boolean {
    return this.fixed;
  }

  now(): number {
    return this.fixed ? this.fixedTime : this.fixedTime + performance.now() / 1000 - this.startedAt;
  }

  setTime(time: number): void {
    this.fixedTime = Math.max(0, time);
  }

  setFixed(fixed: boolean): void {
    this.fixed = fixed;
  }
}
