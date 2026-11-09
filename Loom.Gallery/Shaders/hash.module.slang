// Shared random-number helpers for the gallery's shaders. Textually included via `#include "hash.module.slang"`
// (the *.module.slang naming keeps it out of the standalone shader-compile glob).
//
// Integer arithmetic alone, so every GPU and driver computes the same bits. The common frac(sin(n) * 43758.5453)
// does not: sin's last bits differ between GPUs and drivers, and the large multiply makes those bits the whole
// answer, so anything scattered by it lands somewhere else on every machine and a golden image holds only where
// it was made.

// A random number from 0 to 1 for an integer: the PCG hash, keeping its top 24 bits so the float is exact.
float Hash01(uint v)
{
	uint state = v * 747796405u + 2891336453u;
	uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
	return float(((word >> 22u) ^ word) >> 8) / 16777216.0;
}
