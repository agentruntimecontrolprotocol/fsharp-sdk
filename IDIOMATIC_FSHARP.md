# Idiomatic F# for Public SDKs

A senior-engineer's opinionated style guide. Optimized for SDK authors and Claude Code consumption. Rules are stated as imperatives. Deviations require a comment justifying the deviation.

---

## 1. Core Philosophy

- **Make illegal states unrepresentable.** If the type system can enforce it, the type system must enforce it.
- **Total functions over partial.** No exceptions in normal control flow. Reserve `exception` for truly exceptional, unrecoverable conditions.
- **Data, then behavior.** Define types first. Functions operate on them.
- **Composition over abstraction.** Small functions piped together beat class hierarchies every time.
- **Explicit over implicit at the API boundary.** Inside a module, lean on inference. At the public surface, annotate everything.
- **The compiler is the first reviewer.** If a warning fires, it is an error. Treat warnings as errors in CI.

---

## 2. Project & File Layout

- One primary type or one cohesive module per file. If a file declares two unrelated types, split it.
- Files are ordered by dependency (F# enforces this). Use this constraint as a design tool — circular dependencies are a code smell the compiler refuses to compile.
- Folder structure mirrors namespace structure. No exceptions.
- `Domain/` for types and pure logic. `Infrastructure/` for IO. `Api/` for the public surface. Never the reverse direction in dependencies.
- Public SDK assemblies expose **one root namespace** (e.g. `Acme.WidgetSdk`). All else is `internal`.
- Use `.fsproj` `<Compile Include>` ordering deliberately. Do not rely on alphabetical glob.

### File-length budgets

- **Target:** ≤ 200 lines per `.fs` file.
- **Hard ceiling:** 300 lines. If you cross it, split the file. No exceptions without a written justification in the file header.
- **Function-length target:** ≤ 30 lines.
- **Function-length ceiling:** 50 lines. Extract helpers.
- **Line length target:** 80 chars. **Ceiling:** 100 chars. Break pipelines, multi-line signatures, and long string interpolations.

---

## 3. Naming

- `PascalCase` for modules, namespaces, types, union cases, record fields, public functions, and public values.
- `camelCase` for locals, parameters, and `let private` values.
- No abbreviations in the public API. `cfg` is fine inside a 5-line function; `Configuration` is required on a public record field.
- Boolean predicates start with `is`, `has`, `can`, or `should`. `isExpired`, not `expired`.
- Functions returning `Result`/`Option` are named for the success case: `parse`, `tryFind`, not `parseOrFail`.
- Single-case DUs wrapping primitives use the domain name: `type CustomerId = CustomerId of Guid`, never `type Id`.

---

## 4. Type Design

- **Records for data.** Always. Classes only when interop demands it.
- **Discriminated unions for choice.** If a value has 2+ shapes, it is a DU. Never a record with nullable fields representing variants.
- **Single-case DUs for every primitive in the domain.** `EmailAddress`, `UserId`, `Cents` — never bare `string`, `Guid`, `int`.
- `[<Struct>]` on small (≤ 16 bytes), frequently-allocated value types and single-case wrappers.
- `[<RequireQualifiedAccess>]` on every public DU and module. Prevents downstream name clashes.
- `[<NoComparison; NoEquality>]` on types that should not be compared structurally (most notably types holding closures, IO handles, or mutable state).
- Anonymous records (`{| Foo = 1 |}`) are allowed **internally only**. Never in a public signature.
- Tuples are allowed for local return values. Never in a public signature — use a named record.
- Avoid `obj`, `box`, and `:>` in public surface. If you need polymorphism, use a DU or a generic parameter with constraints.

---

## 5. Error Handling

- `Result<'T, 'Error>` for expected failures with a domain-meaningful error type. Never `Result<'T, string>` in public APIs — define an error DU.
- `Option<'T>` for absence. Never `null`. Never `Nullable<'T>` (except at C# interop boundaries).
- `exception` for programmer errors and truly exceptional IO failure. Never as a control-flow mechanism.
- Define a top-level `type SdkError = ...` DU for the SDK. Every public function returning `Result` returns `Result<'T, SdkError>`.
- Provide a `toException : SdkError -> exn` adapter for consumers who prefer exceptions, but never throw internally.
- Use a `result { }` computation expression (from FsToolkit.ErrorHandling or hand-rolled) for chaining. Never nest matches more than two deep.

---

## 6. Functions

- **Public functions:** full type annotations on parameters and return type. The signature is documentation.
- **Private/internal functions:** lean on inference unless ambiguous.
- **Currying:** preserve it inside F# code. **Uncurry at the C# interop boundary** by exposing `Func<>`-shaped overloads.
- **Pipelines (`|>`) over nested application.** Every transformation chain reads top-to-bottom or left-to-right.
- **`>>` for point-free composition is acceptable** when both functions have obvious types. Avoid when the intermediate type is non-obvious.
- One return type. Functions that "sometimes return X, sometimes Y" are two functions or one function returning a DU.
- Side effects go at the edges. A function whose name does not include a verb of action (`save`, `send`, `write`, `fetch`) does not perform IO.

---

## 7. Async & Concurrency

- **Public SDK APIs return `Task<'T>` / `Task`**, not `Async<'T>`. Reason: C# interop. F#-only consumers can adapt with `Async.AwaitTask`.
- Use the `task { }` CE for IO-bound work. Reserve `async { }` for F#-internal pipelines that need cancellation composition.
- **Every public async method accepts `CancellationToken` as the last parameter.** No exceptions.
- Never `.Result` or `.Wait()`. Never `Async.RunSynchronously` outside test code or `Main`.
- Do not swallow `OperationCanceledException`. Let it propagate.

---

## 8. C# Interop Surface

If the SDK is consumed by C# (it almost certainly is), the public surface must look like idiomatic .NET:

- No `Option<'T>` in signatures — use nullable reference types with `[<AllowNullLiteral>]` discipline, or expose `TryGet` patterns.
- No `Result<'T, 'E>` — provide both `Result`-returning F# functions and exception-throwing or `(bool, out T)` C# overloads as needed.
- No F# `list`, `Map`, `Set` — expose `IReadOnlyList<'T>`, `IReadOnlyDictionary<,>`, `IReadOnlySet<'T>`.
- No tuples — named records.
- No curried functions — `Func<>` / `Action<>`.
- Apply `[<CompiledName("PascalCase")>]` to any `camelCase` function or value that escapes the assembly.
- Apply `[<Extension>]` to enable C# extension-method syntax where useful.
- Hide F# implementation modules behind `[<AutoOpen>] module internal` and re-export a curated `Sdk` namespace.

---

## 9. Modules vs Namespaces

- **Namespaces** at the file top organize the assembly.
- **Modules** group related functions over a type. Convention: `module CustomerId` lives next to `type CustomerId`.
- `[<RequireQualifiedAccess>]` on every public module unless the module is genuinely meant to be opened (rare — `Result`, `Option` style).
- `[<AutoOpen>]` is **forbidden** in public surface. Acceptable internally for DSLs and CE builders only.

---

## 10. Pattern Matching

- Exhaustiveness is non-negotiable. Treat the incomplete-match warning as an error.
- Prefer `match` over `function` in public code — `match` reads better with multiple parameters and named subjects.
- Active patterns for parsing, classification, and flattening nested matches. A 4-deep nested match becomes a 1-deep match with named active patterns.
- Guard clauses (`when`) are allowed but limited to a single boolean expression. If you need more, extract a predicate.

---

## 11. Mutability

- `mutable` is forbidden outside performance-critical inner loops and CE builders. A `mutable` keyword in a public type is a code review block.
- `ref` cells: same rule.
- For accumulation, use `List.fold`, `Seq.fold`, or `Array.fold`. Not a mutable accumulator.
- For builders, prefer immutable record-with-update (`{ x with Field = ... }`) over mutable state.

---

## 12. Collections

- Pick the right one and commit:
  - `Array` — fixed size, random access, perf-sensitive, interop.
  - `List` — pattern matching, small-to-medium, FP transforms.
  - `Seq` — lazy, IEnumerable interop, large/infinite streams.
  - `Map` / `Set` — keyed lookup, immutable.
- Never expose `Seq` in a public signature where the result is enumerated more than once — return `IReadOnlyList`.
- Use the collection module functions (`List.map`, `Seq.filter`) over comprehensions in production code. Comprehensions are fine for tests and scripts.

---

## 13. Documentation

- Every public type, function, and module gets a `///` XML doc comment. Non-negotiable.
- The comment states **what** and **when to use**, not how. Implementation details belong in `//` comments inside the body.
- `<summary>`, `<param>`, `<returns>`, and `<exception>` (if any) are all required.
- Include one `<example>` per non-trivial public function.
- Generate and ship the `.xml` file. Verify it's referenced in the `.nupkg`.

---

## 14. Testing

- One test project per source project. Name: `Acme.WidgetSdk.Tests`.
- Use Expecto or xUnit. Pick one per repo.
- Property-based tests (FsCheck) for every pure function with non-trivial input space.
- Tests are documentation. Name them as sentences: ``[<Fact>] let ``parse returns Error when input is empty`` () = ...``.
- One assertion per test, or one logical assertion via a custom combinator. No mega-tests.
- No `Thread.Sleep`. No real network. No real filesystem outside `tempfile` patterns.

---

## 15. Dependencies & Versioning

- Minimize transitive dependencies. Every package in `paket.dependencies` / `<PackageReference>` is a liability.
- Pin major versions in the SDK's `.fsproj`. Use floating minor only if you trust the maintainer.
- Apply SemVer rigorously. Adding a case to a public DU is a **breaking change**. Document it in CHANGELOG.
- Use `InternalsVisibleTo` only for the test assembly, never for sibling production assemblies.

---

## 16. Reducing Complexity

The single most important section. Complexity compounds; simplicity compounds harder.

### Quantitative limits

- **Cyclomatic complexity per function:** ≤ 7. Hard ceiling 10. Use a linter (e.g. FSharpLint) to enforce.
- **Function length:** target 30 lines, max 50.
- **File length:** target 200 lines, max 300.
- **Line length:** target 80, max 100.
- **Match arms per `match`:** ≤ 8. If you need more, the type is wrong or you need an active pattern.
- **Parameter count per function:** ≤ 4. If you need more, introduce a parameter record.
- **Nesting depth (let bindings, matches, if-then-else):** ≤ 3.

### Tactics

- **Extract until it hurts.** A name is cheaper than a comment.
- **Replace nested matches with active patterns.** A 4-deep match becomes a 1-deep match.
- **Replace boolean flags with DU cases.** `createUser : string -> bool -> bool -> User` → `createUser : string -> UserOptions -> User` where `UserOptions` is a record, or split into multiple constructors.
- **Replace `if`-chains with `match` on a DU.** If the conditions are mutually exclusive states, model them.
- **Hoist constants to the top of the module.** A magic number in the middle of a function is a bug waiting to happen.
- **Inline only once.** If a helper is used in one place and is < 5 lines, inline it. If it grows back, extract it again with a better name.
- **Delete dead code aggressively.** The compiler warns; the linter blocks the build.
- **One level of abstraction per function.** Mixing high-level orchestration with low-level byte manipulation in the same function is the most common complexity smell.

---

## 17. Performance (Last)

- Profile first. Optimize never, until profiled.
- `[<Struct>]` on small types in hot paths.
- `ValueOption`, `ValueTuple` in hot paths if allocation profiling demands.
- `inline` only on small generic helpers where call-site specialization matters. Never as a default.
- `Span<'T>` and `Memory<'T>` are tools, not defaults.

---

## 18. Forbidden in Public APIs

- `null` returns.
- `obj` parameters or returns.
- `exception` as control flow.
- Tuples in signatures.
- Anonymous records in signatures.
- F#-only collection types (`list`, `Map`, `Set`).
- `Async<'T>` (use `Task<'T>`).
- Curried function exports without `[<CompiledName>]` and `Func<>` adapters.
- `mutable` fields on public records.
- `[<AutoOpen>]` modules.
- Two-letter abbreviations as identifiers.

---

## 19. Required in Public APIs

- XML doc comments on every public member.
- `[<RequireQualifiedAccess>]` on every public module and DU.
- `CancellationToken` as the last parameter of every async method.
- Explicit return type annotations on every public function.
- A domain `SdkError` type for every fallible operation.
- A CHANGELOG entry for every release.
- A migration note for every breaking change.

---

## 20. Quick Reference — The Five Smell Tests

If any answer is "yes," refactor before merging:

1. Does any function exceed 50 lines?
2. Does any file exceed 300 lines?
3. Does any public signature mention `obj`, `null`, a tuple, or `Async<>`?
4. Is there a `match` more than 2 levels deep?
5. Is there a `mutable` outside a perf-critical inner loop?
