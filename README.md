GameAI.Net



GameAI.Net is a high-performance embedded realtime AI compute library for games and simulations on .NET.

It is designed for Unity-style workloads where you need thousands of agents per frame with predictable latency and minimal GC pressure.



Core focus: fast,low-level “AI bricks” (behavior ticking,utility scoring,graph propagation,quota selection).

Non-goals: providing a full “game AI framework” or editor tooling — you keep full control and build gameplay logic on top.



---



Why GameAI.Net



Modern game AI often hits the same constraints:

60 FPS target → 16.6 ms per frame

AI budget is typically ~1–4 ms

GC spikes and poor cache locality can break frame pacing



GameAI.Net provides a minimal set of optimized primitives that you can use to build:

Behavior/decision loops for 1k–10k+ NPCs

Batched scoring (priority/LOD/target selection) with SIMD

Influence/emotion propagation over sparse graphs

Crowd “director” selection with quotas (no sorting of the whole population)



---



Key Features (the “bricks”)



1\) UltraFast Behavior Ticking

Array-based Behavior Tree runtime (no node objects in the hot path)

TickFast(...) path for pure execution

Optional metrics path (if you enable it)



2\) SIMD Utility Scoring (Batch Dot Products)

Compute score\[i] = dot(features\[i],weights) for large batches

Uses SlidingRank.FastOps SIMD dot implementation



3\) Sparse Influence / Emotion Graph

CSR-style adjacency (sparse graph)

Step(...) (full step) and StepActive(...) (only active sources)

Designed for “update every N frames” scheduling



4\) Crowd Selection With Quotas (Zero-Alloc)

Select top candidates per group quota

No full sort,no per-call allocations (SelectWithQuotasInto(...))

Fast-path for small quotas via stackalloc



5\) Cache-Friendly SoA Storage (Recommended)

BlackboardSoA stores data contiguously for better locality

BehaviorTreeSoA reads SoA blackboard directly



---



Installation



NuGet

```bash

dotnet add package GameAI.Net

```





If you ship SlidingRank as a dependency package,NuGet will pull it automatically.



Unity usage

GameAI.Net is engine-agnostic. In Unity,you typically:

keep one “world” tick (one Update loop) and process agents in a tight loop

avoid per-NPC Update() calls for large populations



---



Quick Start (C#)



Behavior Tree (Jagged blackboard)

```csharp

using GameAI.Net.BehaviorTree;

using GameAI.Net.Core;

using GameAI.Net.FastCalc;



// Condition:float\[0] > 0.5

var cond = new ConditionProgram(new\[]

{

&nbsp;new Instruction(OpCode.LoadFloat,argI:0),

&nbsp;new Instruction(OpCode.PushConst,argF:0.5f),

&nbsp;new Instruction(OpCode.Gt)

});



var conds = new\[] { cond };



// Tree:Selector( Sequence(Condition,Attack),Idle )

var nodes = new\[]

{

&nbsp;new Node(NodeKind.Selector,0,0),// 0

&nbsp;new Node(NodeKind.Sequence,0,0),// 1

&nbsp;new Node(NodeKind.Condition,0,0),// 2 (condId=0)

&nbsp;new Node(NodeKind.Action,1,0),// 3 Attack

&nbsp;new Node(NodeKind.Action,0,0),// 4 Idle

};



var children = new\[] { 1,4,2,3 };

var childStart = new\[] { 0,2,4,4,4 };

var childCount = new\[] { 2,2,0,0,0 };



var actions = new MyActions(); // implement IActionHandler

var tree = new BehaviorTree(nodes,children,childStart,childCount,conds,actions);



// One agent blackboard

var bb = new Blackboard(floats:new float\[8],ints:new int\[4]);

bb.SetFloat(0,0.9f); // aggro



var ctx = new TickContext(1f/60f,bb);

tree.TickFast(ctx);

```



Behavior Tree (SoA blackboard — recommended for large batches)

```csharp

using GameAI.Net.BehaviorTree;

using GameAI.Net.Core;



// Create SoA storage for many agents

var bb = new BlackboardSoA(agentCount:10000,floatSlots:8,intSlots:4);



// Tick the tree for agent i

treeSoA.TickFast(deltaTime:1f/60f,bb,agent:i);

```



SIMD Utility Scoring (batch)

```csharp

using GameAI.Net.Utility;

using SlidingRank.FastOps;



var data = new float\[count \* dim]; // features matrix (row-major)

var features = new EmbeddingMatrix(data,count,dim);



var weights = new float\[dim];

var scores = new float\[count];



UtilityScorer.ScoreAll(features,weights,scores);

```



Emotion / Influence Graph

```csharp

using GameAI.Net.Emotion;



// CSR graph:start has length npcCount+1,edges in to\[] and w\[]

var graph = new EmotionGraph(npcCount,start,to,w);



// Full step (all sources)

graph.Step(dt:1f/60f);



// Active sources only (recommended)

graph.StepActive(activeSources,dt:1f/60f);

```



Crowd Selection (zero-alloc)

```csharp

using GameAI.Net.Crowd;



Span<int> output = stackalloc int\[100]; // sum of quotas

int written = CrowdDirector.SelectWithQuotasInto(candidates,quotas,output);

```

---



Threading Model



Recommended default: run serial ticks in a tight loop (zero allocations,stable frame pacing).



Parallel path: available and useful for large batches (e.g. 50k–100k scoring rows).

Note:TPL (Parallel.For) may allocate small framework overhead. For strict zero-alloc multithreading,integrate with Unity Jobs/DOTS or a custom worker pool in your host application.



---



Performance Benchmarks (Summary)



Benchmarks were executed on:

CPU:Intel Core i5-11400F (6C/12T)

OS:Windows 11

.NET:8.0.23

BenchmarkDotNet:0.15.8



Three curated scenarios:



Small: 1,000 NPC; utility 10k×32; crowd 10k; emotion 10k nodes; emotion update every 4 frames; 10% active sources.

Medium: 5,000 NPC; utility 50k×64; crowd 50k; emotion 50k nodes; emotion update every 4 frames; 10% active sources.

Large: 10,000 NPC; utility 100k×64; crowd 100k; emotion 100k nodes; emotion update every 4 frames; 10% active sources.



Selected results (module-level):



BT TickFast (10k NPC): ~297 µs (Jagged) / ~285 µs (SoA)

Utility scoring (100k×64): ~4.68 ms (serial) / ~1.16 ms (parallel)



Frame budget benchmark (FrameN\_\*) runs N=4 frames per invocation (emotion update once per 4 frames).

Per-frame latency is Mean / 4.



---



Commercial Licensing \& Pricing



GameAI.Net is a commercial library.



Pricing (typical for game middleware)

Indie — $49 / month

1 project

Up to 1,000 active agents (concurrent)



Studio — $499 / month

Up to 3 projects

Up to 10,000 active agents




Enterprise / AAA — from $2,999 / month

Unlimited projects and agents




How it works commercially:
Evaluate freely
Commercial use requires a paid license



“Active agents” means agents ticked per frame (or per AI update).

Pricing can be adjusted for custom licensing terms (one-time fee,per-seat,per-title,or revenue-based).



How to buy / contact

Email:vipvodu@yandex.ru

Telegram:@vipvodu



---



License

See LICENSE.txt (Commercial License / EULA).



---



Roadmap (short)

Optional strict zero-alloc multithreading module (worker pool)

Unity integration package (UPM) with sample scenes and profiling overlays

More built-in templates (combat/stealth/crowd LOD scheduling) as examples (not required for core)



---



Disclaimer

This project focuses on low-level performance primitives. You are expected to implement game-specific logic (navigation,animation,combat,dialogue,network sync) in your game code.





---

