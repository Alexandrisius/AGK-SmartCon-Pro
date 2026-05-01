# Security Policy

## Supported Versions

Only the **latest stable release** receives security updates. Older versions are not supported.

| Version | Supported |
| ------- | --------- |
| Latest  | ✅        |
| Older   | ❌        |

## Reporting a Vulnerability

**Do not open a public issue for security vulnerabilities.**

Instead, use one of these channels:

1. **GitHub Private Security Advisory** (preferred):
   [Create advisory](https://github.com/Alexandrisius/AGK-SmartCon-Pro/security/advisories/new)
2. **Direct contact** with the repository maintainer (see `.github/CODEOWNERS`)

We aim to respond within **7 business days**. After confirmation, a fix will be published as a patch release with credit to the reporter (unless you prefer to remain anonymous).

## Scope

**In scope:**

- Arbitrary code execution via a loaded Revit project or family file
- Credential or token leaks through logs or configuration files
- Unsafe deserialization of JSON mapping data
- SQLite injection in the FamilyManager catalog

**Out of scope:**

- Issues specific to a particular Revit installation
- Social engineering attacks
- Denial of service through excessively large project files
