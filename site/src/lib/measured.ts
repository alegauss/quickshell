// QS385. What the site says about start-up is read from the measurement, not typed beside it.
// The figure is a budget in docs/PERFORMANCE.md (400 ms), and a budget printed where a result
// belongs is a claim the first reader who runs the harness disproves. So the cold-start figure,
// its date and its machine come from benchmarks/results/startup-h.md, through
// scripts/measured.mjs, which runs before every build and fails it when the verdict cannot be
// read.
import { startMachine, startMeasuredMs, startMeasuredOn } from "./measured.generated";

export { startMachine, startMeasuredMs, startMeasuredOn };

/** The budget, figure 5 of docs/PERFORMANCE.md. */
export const startBudgetMs = 400;

/** Whether the measurement meets the budget, which is what decides how the figure is worded. */
export const startMet = startMeasuredMs < startBudgetMs;
