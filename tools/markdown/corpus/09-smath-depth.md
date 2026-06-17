Sub/superscript chains build nested <mrow> in smath. This is BOUNDED (by MAX_TEX /
MAX_NODES) and TOTAL, but the depth tracks input length, not MAX_DEPTH=32. A modest
example (kept small so it stays a valid, in-bounds case for the oracle):

$$x__________________________________________________________________________$$

$$a^b^c^d^e^f^g^h^i^j^k^l^m^n^o^p^q^r^s^t^u^v^w^x^y^z$$

Inline math is separately capped at 1024 chars by the inline scanner: $______$.
