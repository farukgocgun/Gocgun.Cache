# Gocgun.Cache

> **Beta.** This package is in beta: the public API may change between releases.

Byte cache for a single process plus Redis.

`GetAsync`, `SetAsync`, and `RemoveAsync` store a `byte[]`. The caller serializes protobuf (or anything else) before `SetAsync`. This package does not parse those bytes.

L1 is `IMemoryCache`. L2 is `StackExchange.Redis` 2.x. `RemoveAsync` deletes both layers and publishes a pub/sub notice so other processes drop their L1 entry. `RemoveByPrefixAsync` deletes matching L2 keys with `SCAN` (`IServer.KeysAsync`). It does not send `KEYS`.

There are no cache tags, no stampede lock, and no tenant prefix. A `KeyPrefix` option is an application prefix only.

License: MIT. See `LICENSE`.

## Publishing to nuget.org

Run `scripts/publish.sh` (or `scripts/publish.ps1` on Windows). It dispatches the manual `release.yml` workflow of each dependency first, in dependency order (Gocgun.Cache), then of this package, waiting for each run and stopping on the first failure. Requires the GitHub CLI signed in with `gh auth login`; the `nuget` environment and the nuget.org Trusted Publishing policy must exist in each repository.
