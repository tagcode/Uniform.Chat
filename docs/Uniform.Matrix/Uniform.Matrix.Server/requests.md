# Requests

ASP.NET Core minimal APIs, one `Map…` extension per spec area in `Endpoints/`. Each endpoint reads the request, calls a service and returns JSON.

- `MatrixHttp.HandleAsync` wraps every request: CORS headers, and [standard errors](https://spec.matrix.org/latest/client-server-api/#standard-error-response) from exceptions. A `MatrixException` becomes its status and `errcode`; `MatrixErrorException` adds fields such as `current_version`; `AuthenticationRequiredException` answers 401 with the [user-interactive auth](https://spec.matrix.org/latest/client-server-api/#user-interactive-authentication-api) flows; bad JSON becomes `M_BAD_JSON` or `M_NOT_JSON`; unknown paths `M_UNRECOGNIZED`.
- `RequireAccessToken()` on a route group checks the token from the `Authorization` header (or the deprecated `access_token` query parameter); handlers get the user with `http.GetRequester()`, or `http.GetOwnRequester(userId)` for endpoints that take a user ID and must refuse other users.
- Access tokens are stored as SHA-256 hashes, so a leaked database does not leak sessions.
