export const meta = {
  name: 'markdown-renderer-study',
  description: 'Characterize the markdown renderer lexers as state machines, adversarially audit them, and design a cleaner/simpler system',
  phases: [
    { title: 'Characterize', detail: 'formalize each lexer (block/inline/math/highlight) as a state machine + regex inventory' },
    { title: 'Audit', detail: 'adversarially find + execute-verify contract violations (throw/hang/unbounded/F4/incorrect)' },
    { title: 'Design', detail: 'panel: per-char vs per-line, regex elimination, unification, simplification' },
    { title: 'Judge', detail: 'score design proposals and synthesize a recommendation' },
  ],
};

const REPO = '/home/rijos/Projects/gert';
const WWW = REPO + '/src/Gert.Api/wwwroot';
const HARNESS = REPO + '/tools/markdown';

const CONTEXT = `
You are studying Gert's in-house, no-npm, security-first markdown renderer. Source files:
- ${WWW}/lib/markdown.js          (thin facade: normalize -> parseBlocks -> render -> assignHeadingIds, ONE try/catch, total)
- ${WWW}/lib/render/lines.js      (BLOCK lexer: PER-LINE, classifyLine over a frozen ordered LINE_KINDS table; parseBlocks)
- ${WWW}/lib/render/inline.js     (INLINE lexer: PER-CHARACTER single scan tokenizeInline + delimiter-stack finalizeEmphasis)
- ${WWW}/lib/render/dom.js        (structural renderer: AST -> DOM via ONE guarded createEl over a closed (ns,tag) allow-list)
- ${WWW}/lib/render/url.js        (sanitizeUrl/sanitizeImgUrl/isExternal/slugify - the F4 URL chokepoint)
- ${WWW}/lib/smath.js             (MATH lexer: PER-CHARACTER lex() (regex-free) + bounded recursive-descent -> native MathML)
- ${WWW}/lib/highlight.js         (HIGHLIGHT lexer: PER-CHARACTER scan driven by sticky /y regex rules per language)
- ${WWW}/lib/component.js, ${WWW}/components/canvas/artifacts/md-math.js, md-code.js (the leaves)

Design contracts (docs/design/security.md F4, and the module headers):
- TOTAL: renderMarkdown never throws; any fault degrades to literal source text.
- BOUNDED: container/emphasis nesting is depth-capped (MAX_NEST=32, MAX_INLINE=32, smath MAX_DEPTH=32, MAX_NODES=6000); scans are O(cap) not O(n^2); no catastrophic-backtrack regex.
- SECURE (F4): no raw HTML interpreted; closed element + per-tag attribute allow-list; href/src scrubbed through one chokepoint; data:image only; MathML has no href/src sink; fenced code emits only inert tok-* spans.

A HEADLESS test harness already exists and runs the REAL unmodified renderer in Node (zero deps, custom DOM shim + ESM loader):
- ${HARNESS}/lib/render.mjs   exports: renderMarkdown(src)->fragment, parseDocument(src)->block AST, sanitizeUrl, smath, lines, inline
- ${HARNESS}/lib/oracle.mjs   exports: checkSecurity(frag), checkBounds(frag,srcLen), checkWellFormed(frag), serialize(node), elements(root)
To run a probe: cd ${HARNESS} && write a UNIQUE file probe-<random>.mjs that imports "./lib/render.mjs" and "./lib/oracle.mjs", run \`node probe-<random>.mjs\`, then delete it. Node v24 is available.
`;

// ---------------------------------------------------------------- Characterize
const SPEC_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['lexer', 'granularity', 'usesRegex', 'stateMachine', 'regexInventory', 'invariants', 'simplifications', 'summary'],
  properties: {
    lexer: { type: 'string' },
    granularity: { type: 'string', enum: ['per-line', 'per-character', 'hybrid'] },
    usesRegex: { type: 'boolean' },
    stateMachine: {
      type: 'object', additionalProperties: false,
      required: ['alphabet', 'states', 'bounds'],
      properties: {
        alphabet: { type: 'string', description: 'the input tokens/symbols the machine consumes' },
        states: { type: 'array', items: { type: 'object', additionalProperties: false, required: ['name', 'role', 'transitions'],
          properties: { name: { type: 'string' }, role: { type: 'string' },
            transitions: { type: 'array', items: { type: 'object', additionalProperties: false, required: ['on', 'to', 'action'],
              properties: { on: { type: 'string' }, to: { type: 'string' }, action: { type: 'string' } } } } } } },
        bounds: { type: 'array', items: { type: 'string' } },
      },
    },
    regexInventory: { type: 'array', items: { type: 'object', additionalProperties: false, required: ['pattern', 'purpose', 'redosRisk'],
      properties: { pattern: { type: 'string' }, purpose: { type: 'string' }, redosRisk: { type: 'string', enum: ['none', 'low', 'medium', 'high'] }, note: { type: 'string' } } } },
    invariants: { type: 'array', items: { type: 'string' } },
    simplifications: { type: 'array', items: { type: 'object', additionalProperties: false, required: ['idea', 'rationale', 'risk', 'effort'],
      properties: { idea: { type: 'string' }, rationale: { type: 'string' }, risk: { type: 'string' }, effort: { type: 'string', enum: ['small', 'medium', 'large'] } } } },
    summary: { type: 'string' },
  },
};

phase('Characterize');
const LEXERS = [
  { key: 'block', file: 'lib/render/lines.js', hint: 'PER-LINE classifier (LINE_KINDS) + parseBlocks. Describe states as the dispatch/continuation modes; alphabet = line-kinds. Note every regex used and its purpose.' },
  { key: 'inline', file: 'lib/render/inline.js', hint: 'PER-CHARACTER scanner tokenizeInline + delimiter-stack finalizeEmphasis. Describe the char-level states (text/code/math/link/emphasis) and the emphasis pairing as a pushdown.' },
  { key: 'math', file: 'lib/smath.js', hint: 'PER-CHARACTER lex() (no regex but for /[a-zA-Z]/ char-class) + bounded recursive-descent parser. Describe lexer states and the parser grammar/bounds.' },
  { key: 'highlight', file: 'lib/highlight.js', hint: 'PER-CHARACTER position scan that tries sticky /y regex rules in order. The regex rules ARE the state machine per language. Inventory EVERY rule and rate ReDoS risk (esp. backreferences R"()", raw strings, verbatim @"", quote alternations).' },
];
const specs = await parallel(LEXERS.map((L) => () =>
  agent(`${CONTEXT}

TASK: Read ${WWW}/${L.file} in full and produce a PRECISE, formal state-machine description of the "${L.key}" lexer. ${L.hint}

Requirements:
- granularity: classify the lexer as per-line, per-character, or hybrid (be exact about THIS lexer).
- stateMachine: give the input alphabet, the named states with their role, and the transitions (on input -> to state, with the emit/action). Keep it faithful to the actual code, not an idealized version.
- regexInventory: list EVERY regular expression the lexer uses, its purpose, and a ReDoS risk rating (none/low/medium/high) with a one-line justification. For 'highlight', this is the core deliverable - examine each language's rules for catastrophic backtracking (nested quantifiers, overlapping alternations, backreferences with lazy quantifiers).
- invariants: what totality/termination/bound/security properties this lexer maintains, and HOW (the specific cap or guard).
- simplifications: concrete, capability-preserving ways to make this lexer cleaner or simpler (e.g. replace a regex with a char-class check, collapse duplicate logic, table-drive a switch). Rate risk and effort.
Return ONLY the structured object.`, { label: `spec:${L.key}`, phase: 'Characterize', schema: SPEC_SCHEMA })
));
const cleanSpecs = specs.filter(Boolean);

// ---------------------------------------------------------------- Audit
const FINDINGS_SCHEMA = {
  type: 'object', additionalProperties: false, required: ['findings'],
  properties: { findings: { type: 'array', items: {
    type: 'object', additionalProperties: false,
    required: ['category', 'input', 'observed', 'verifiedBy', 'severity', 'isReal'],
    properties: {
      category: { type: 'string', enum: ['throw', 'hang', 'unbounded', 'security', 'incorrect', 'none'] },
      input: { type: 'string', description: 'the exact markdown input that triggers it (keep < 2KB)' },
      observed: { type: 'string' },
      verifiedBy: { type: 'string', description: 'the exact command/measurement that reproduced it' },
      severity: { type: 'string', enum: ['low', 'medium', 'high'] },
      isReal: { type: 'boolean', description: 'true only if you actually reproduced it via node' },
    } } } },
};

phase('Audit');
const AUDIT_LENSES = [
  { key: 'totality', focus: 'Try to make renderMarkdown THROW or return a non-fragment. Try half-open fences/math, unbalanced brackets/emphasis, lone surrogates, NULs, huge entity refs, deeply nested quotes/lists at exactly the cap boundary, malformed tables, setext edge cases.' },
  { key: 'redos-highlight', focus: 'Hunt catastrophic backtracking in highlight.js. For EACH language rule with risk (csharp @"(?:[^"]|"")*", cpp/rust raw-string backreferences R"x(...)x" and r#"..."#, block comments, quote/escape alternations), build inputs of growing size (e.g. 1k/2k/4k/8k/16k of an adversarial pattern in a fenced block) and MEASURE render time. Report any clearly super-linear growth as category=hang.' },
  { key: 'complexity-md', focus: 'Hunt super-linear time in the BLOCK and INLINE markdown lexers (not highlight): walls of "*"/"_"/"~", many "[" / "](", nested code spans, long autolink-like runs, pathological tables (many columns x many rows), "> "xN, list markers. Measure render time across sizes; report super-linear cases.' },
  { key: 'bounds-memory', focus: 'Try to make output node-count or depth explode relative to input (amplification): emphasis nesting, list/quote nesting, table cell multiplication, repeated math. Use checkBounds(frag, src.length). Report category=unbounded when the ceiling is exceeded OR growth is clearly super-linear.' },
  { key: 'security-f4', focus: 'Try to bypass F4 using checkSecurity(frag): smuggle a live element, an on* handler, a style attribute, an unsafe href/src, a forged <a>/<img> from math (\\href/\\src), entity/control-char scheme tricks, data: image variants, autolink edge cases. Report any non-null checkSecurity as category=security.' },
  { key: 'correctness', focus: 'Find clearly WRONG output (not crashes): emphasis mis-pairing, list/table mis-parsing, math/currency confusion, code-span boundary errors, heading-id collisions, link-title parsing. Compare against CommonMark/GFM intent. Report category=incorrect with the expected-vs-actual.' },
];
const auditRaw = await parallel(AUDIT_LENSES.map((A) => () =>
  agent(`${CONTEXT}

TASK (audit lens: ${A.key}): ${A.focus}

METHOD - this is EXECUTION-VERIFIED auditing, not speculation:
1. cd ${HARNESS}; write probe-${A.key}-<random>.mjs importing "./lib/render.mjs" and "./lib/oracle.mjs".
2. Run candidates through renderMarkdown + the oracle checks; for timing use process.hrtime.bigint() across input sizes (1k,2k,4k,8k,16k...) and look for super-linear growth.
3. Only report a finding with isReal:true if you ACTUALLY reproduced it. Put the reproducing command/measurement in verifiedBy.
4. Keep inputs under 2KB in the report (for a hang, report the GENERATOR/pattern + the size at which it blows up, not a 1MB string).
5. Clean up your probe file when done.
If you find nothing real for this lens, return findings:[] (or a single category:'none' entry noting what you tried). Do NOT invent findings.
Return ONLY the structured object.`, { label: `audit:${A.key}`, phase: 'Audit', schema: FINDINGS_SCHEMA })
));
const allFindings = auditRaw.filter(Boolean).flatMap((r) => r.findings || []).filter((f) => f.isReal && f.category !== 'none');

// Adversarial re-verification of each real finding by an independent skeptic that re-runs it.
const VERDICT_SCHEMA = {
  type: 'object', additionalProperties: false, required: ['reproduced', 'category', 'severity', 'note'],
  properties: {
    reproduced: { type: 'boolean' },
    category: { type: 'string', enum: ['throw', 'hang', 'unbounded', 'security', 'incorrect', 'none'] },
    severity: { type: 'string', enum: ['low', 'medium', 'high'] },
    note: { type: 'string' },
  },
};
const verified = await parallel(allFindings.map((f) => () =>
  agent(`${CONTEXT}

A prior auditor reported this finding about the markdown renderer. Independently REPRODUCE it from scratch (write your own probe, run node). Be skeptical - default reproduced:false unless you see it yourself.
CATEGORY: ${f.category}
INPUT (or generator): ${JSON.stringify(f.input).slice(0, 1500)}
CLAIMED OBSERVATION: ${f.observed}
HOW THEY VERIFIED: ${f.verifiedBy}

For hang/unbounded: confirm super-linear growth by measuring >=3 sizes; a one-off slow run is NOT a hang. For security: confirm checkSecurity returns non-null. For incorrect: confirm the output genuinely violates CommonMark/GFM intent (not just a defensible stricter reading). Report your verdict.`,
    { label: `verify:${f.category}`, phase: 'Audit', schema: VERDICT_SCHEMA })
    .then((v) => ({ finding: f, verdict: v }))
));
const confirmed = verified.filter(Boolean).filter((x) => x.verdict && x.verdict.reproduced)
  .map((x) => ({ ...x.finding, category: x.verdict.category, severity: x.verdict.severity, verifyNote: x.verdict.note }));

// ---------------------------------------------------------------- Design panel
const PROPOSAL_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['angle', 'perCharVsPerLine', 'regexElimination', 'unification', 'simplifications', 'risks', 'summary'],
  properties: {
    angle: { type: 'string' },
    perCharVsPerLine: { type: 'object', additionalProperties: false, required: ['recommendation', 'reasoning'],
      properties: { recommendation: { type: 'string', enum: ['adopt-per-char', 'keep-per-line', 'hybrid'] }, reasoning: { type: 'string' } } },
    regexElimination: { type: 'object', additionalProperties: false, required: ['recommendation', 'reasoning'],
      properties: { recommendation: { type: 'string', enum: ['eliminate-all', 'eliminate-risky', 'keep'] }, reasoning: { type: 'string' } } },
    unification: { type: 'object', additionalProperties: false, required: ['recommendation', 'reasoning'],
      properties: { recommendation: { type: 'string' }, reasoning: { type: 'string' } } },
    simplifications: { type: 'array', items: { type: 'object', additionalProperties: false, required: ['change', 'benefit', 'risk', 'effort'],
      properties: { change: { type: 'string' }, benefit: { type: 'string' }, risk: { type: 'string' }, effort: { type: 'string', enum: ['small', 'medium', 'large'] } } } },
    risks: { type: 'array', items: { type: 'string' } },
    summary: { type: 'string' },
  },
};

phase('Design');
const specsBlob = JSON.stringify(cleanSpecs).slice(0, 12000);
const findingsBlob = JSON.stringify(confirmed.map((f) => ({ category: f.category, observed: f.observed, severity: f.severity }))).slice(0, 4000);
const ANGLES = [
  { key: 'purist', desc: 'A formal-purist: argue for ONE unified per-character, regex-free, table-driven state-machine framework shared by all lexers. Maximize well-definedness and reasoning-about-safety.' },
  { key: 'pragmatist', desc: 'A pragmatist/maintainer: minimize churn and risk. Keep what is already per-char/regex-free (inline, math) and per-line where it is genuinely clearer (block). Target only the highest-value cleanups.' },
  { key: 'security', desc: 'A security/robustness lens: prioritize eliminating ReDoS and unbounded behaviour; whatever design best removes the catastrophic-backtracking surface (highlight) and tightens totality wins.' },
];
const proposals = await parallel(ANGLES.map((A) => () =>
  agent(`${CONTEXT}

You have the formal lexer specs and the EXECUTION-CONFIRMED audit findings:
LEXER SPECS: ${specsBlob}
CONFIRMED FINDINGS: ${findingsBlob || '[none confirmed]'}

TASK: From this angle - ${A.desc} - design how to make the renderer a CLEANER, MORE WELL-DEFINED system with the SAME capabilities. Address specifically:
1. perCharVsPerLine: should the BLOCK lexer move from its current per-line regex classifier to a per-character state machine? (The inline + math lexers are ALREADY per-character; the block lexer is the per-line outlier.) Justify with concrete trade-offs (streaming/markdown line-semantics vs uniformity).
2. regexElimination: should regex be removed? Where is it load-bearing vs gratuitous? Highlight is the only regex-driven lexer and the only ReDoS surface.
3. unification: can the three/four lexers share one well-described state-machine abstraction (e.g. a common "scan with a transition table + bounded stack" core), or is their per-domain shape better kept separate?
4. simplifications: concrete capability-preserving cleanups (rate benefit/risk/effort).
Be concrete and grounded in the actual code. Return ONLY the structured object.`, { label: `design:${A.key}`, phase: 'Design', schema: PROPOSAL_SCHEMA })
));
const cleanProposals = proposals.filter(Boolean);

// ---------------------------------------------------------------- Judge + synthesize
phase('Judge');
const SYNTH_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['verdict_perCharVsPerLine', 'verdict_regex', 'verdict_unification', 'rankedSimplifications', 'recommendedPlan', 'rationale'],
  properties: {
    verdict_perCharVsPerLine: { type: 'string' },
    verdict_regex: { type: 'string' },
    verdict_unification: { type: 'string' },
    rankedSimplifications: { type: 'array', items: { type: 'object', additionalProperties: false, required: ['rank', 'change', 'why', 'effort'],
      properties: { rank: { type: 'number' }, change: { type: 'string' }, why: { type: 'string' }, effort: { type: 'string' } } } },
    recommendedPlan: { type: 'array', items: { type: 'string' }, description: 'ordered, concrete steps' },
    rationale: { type: 'string' },
  },
};
const synthesis = await agent(`${CONTEXT}

Three design proposals (different angles) plus the confirmed audit findings:
PROPOSALS: ${JSON.stringify(cleanProposals).slice(0, 16000)}
CONFIRMED FINDINGS: ${findingsBlob || '[none confirmed]'}

TASK: As an impartial architect, synthesize ONE coherent recommendation. Resolve the disagreements between the proposals with reasoning (do not just average them). Deliver:
- verdict_perCharVsPerLine: the final call on per-char vs per-line for the BLOCK lexer, with the deciding reason.
- verdict_regex: the final call on regex (eliminate where / keep where), grounded in the ReDoS findings.
- verdict_unification: whether and how to share a state-machine abstraction across lexers.
- rankedSimplifications: the concrete cleanups, ranked by value/effort.
- recommendedPlan: an ordered, concrete sequence a maintainer could follow.
Be decisive and specific to THIS codebase. Return ONLY the structured object.`, { label: 'synthesize', phase: 'Judge', schema: SYNTH_SCHEMA });

return {
  specs: cleanSpecs,
  confirmedFindings: confirmed,
  rawFindingCount: allFindings.length,
  proposals: cleanProposals,
  synthesis,
};
