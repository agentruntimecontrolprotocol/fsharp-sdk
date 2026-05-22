# Stream Resume

This recipe ports the TypeScript `stream-resume` pattern to F#. The
writer agent emits a chunked result with `result_chunk` events while the
runtime advertises a resume window on the session.

The sample runs in memory and prints the resume token plus streamed
chunks. A WebSocket host uses the same runtime settings; after a
transport drop, a client reconnects with the resume token and replays
events after its last processed sequence.

Run it:

```sh
dotnet run --project recipes/stream-resume/stream-resume.fsproj
```
