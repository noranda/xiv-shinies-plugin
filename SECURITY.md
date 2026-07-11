# Security Policy

This plugin handles a user credential (the XIV Shinies plugin token) and reads game-client
memory, so security reports are taken seriously.

## Reporting a vulnerability

**Please report privately — do not open a public issue.**

Use GitHub's private vulnerability reporting: the **Security** tab of this repository →
**Report a vulnerability**. You will get a response as quickly as possible, and credit in the
fix's release notes if you want it.

In scope for this repository: anything in the plugin — token handling and storage, what the
plugin reads from the game, what it transmits and to where, and the safety of the backend-URL
override. Issues in the XIV Shinies website or API are handled by the same maintainer and can
be reported the same way; they will be routed to the right place.

## What the plugin promises

- The token is sent only over HTTPS, only as an `Authorization` header, and only to the
  configured backend — which is refused outright if it is a raw IP address or plaintext to a
  remote host. Pointing the plugin at a non-default backend requires an explicit
  acknowledgment, because the token is sent to whatever host is configured.
- The character's raw ContentId never leaves the game process — it is hashed (SHA-256) on the
  machine, and the raw value is never logged or persisted.
- Nothing about other players is ever read or transmitted.

If you find behavior that contradicts any of these, that is exactly the kind of report we
want.
