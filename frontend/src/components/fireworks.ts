import confetti from 'canvas-confetti';

// A celebratory fireworks burst that runs for a few seconds.
export function launchFireworks(durationMs = 4000) {
  const end = performance.now() + durationMs;
  const colors = ['#ff5252', '#ffd740', '#69f0ae', '#40c4ff', '#e040fb'];

  (function frame() {
    confetti({ particleCount: 4, angle: 60, spread: 70, origin: { x: 0 }, colors });
    confetti({ particleCount: 4, angle: 120, spread: 70, origin: { x: 1 }, colors });

    // Occasional center burst for a "firework" pop.
    if (Math.random() < 0.1) {
      confetti({
        particleCount: 80,
        spread: 360,
        startVelocity: 35,
        origin: { x: Math.random() * 0.6 + 0.2, y: Math.random() * 0.4 + 0.1 },
        colors,
      });
    }

    if (performance.now() < end) {
      requestAnimationFrame(frame);
    }
  })();
}
