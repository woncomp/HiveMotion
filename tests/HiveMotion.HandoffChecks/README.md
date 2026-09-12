# Foreground handoff checks

Run the deterministic controller checks without activating any desktop windows:

```powershell
dotnet run --project tests/HiveMotion.HandoffChecks -c Release
```

The harness compiles the production controller with a fake clock, scheduler, and native-operation host. It needs no test framework packages. It covers request/hide ordering, bounded retries, delayed callbacks, target replacement, cancellation, user intervention, and asynchronous restoration/activation.

Desktop verification is still required with a Release build: select normal and minimized windows; cancel to the previous window; select an unresponsive application; close the target during a handoff; reopen the overlay immediately after selecting; and choose another application during retries. Exercise the flows at 60 Hz and high refresh rates and across different monitor DPI settings. Confirm the overlay disappears promptly and old requests do not override a new choice.

The 300 ms deadline stops new requests. It cannot interrupt a native call or retract a request already accepted by Windows. New input in another foreground window conservatively cancels retries, including input that does not produce a foreground-change event.
