# claim-backend

ASP.NET Core 10 Web API (Controllers) for the claim project, secured with AWS Cognito.

## Structure

- `ClaimBackend.Api/` — the API project
  - `Auth/CognitoOptions.cs` — binds the `Cognito` config section
  - `Data/ClaimBackendDbContext.cs` — EF Core DbContext (Npgsql)
  - `Controllers/HealthController.cs` — public health check
  - `Controllers/MeController.cs` — `[Authorize]`-protected example, echoes the caller's JWT claims
  - `Controllers/HelloController.cs` — `[Authorize]`-protected smoke-test endpoint
- `dockerfile-lambda` — container image used for AWS Lambda deployment

## Auth

JWT Bearer auth validates access tokens issued by the Cognito user pool provisioned in
`claim-infrastructure/cognito.tf`:

- `Authority` = `https://cognito-idp.{region}.amazonaws.com/{user pool id}`
- Audience isn't validated (Cognito access tokens have no `aud` claim) — instead the
  `client_id` claim is checked against the configured app client id in `OnTokenValidated`.
- The frontend (`claim-frontend`, via Amplify's hosted-UI redirect flow) should send the
  Cognito **access token** as `Authorization: Bearer {token}`.

Pool id, region and app client id live in `appsettings.json` under `Cognito` — update them
there if the Terraform-managed pool is ever recreated (the ids currently match the
`eu-west-2_o9OmKT1Iy` pool / `6ub55cc2k3so0niq5m3lttn250` client from `claim-infrastructure`).

## Games

Players create a game, share the six-character code it returns, and everyone else joins with
that code. The lobby shows who is in, and the host starts it.

| Method | Route                     | Notes                                              |
| ------ | ------------------------- | -------------------------------------------------- |
| POST   | `/api/games`              | Creates a game; the caller becomes host            |
| GET    | `/api/games/{code}`       | Current roster and status — the lobby polls this   |
| POST   | `/api/games/{code}/join`  | Joins; re-joining is a no-op, not an error         |
| POST   | `/api/games/{code}/leave` | Leaves; hands off the host role, deletes if empty  |
| POST   | `/api/games/{code}/start` | Host only; moves the game from `Lobby` to `InProgress` |

All of them require a Cognito access token.

Codes come from a 32-character alphabet with `I`, `O`, `0` and `1` left out, since they get read
aloud and typed by hand.

Each game is a single DynamoDB item keyed by its code (`Games:TableName`, provisioned in
`claim-infrastructure/games.tf`). DynamoDB rather than Postgres because the Lambda sits outside
the VPC so it can reach Cognito's JWKS endpoint — DynamoDB is reachable over the public AWS API
and needs no NAT Gateway. Writes carry a `Version` attribute used as a condition on the next
write, so two players joining at the same moment can't overwrite each other's change to the
roster; a write that loses re-reads and reapplies. Abandoned lobbies are removed by DynamoDB's
TTL on `ExpiresAt` rather than by the API. That expiry is measured from the most recent write,
so an active game keeps pushing it out; note that polling the lobby is a read and does not.

Note that the display name is sent by the client, because the API is called with the Cognito
*access* token and the name lives on the *id* token. It is therefore self-asserted. Verifying it
would mean calling Cognito's `GetUser` with the caller's access token.

### Running DynamoDB locally

`appsettings.Development.json` points `Games:ServiceUrl` at DynamoDB Local on port 8000. That
setting is absent in AWS, where the SDK's default endpoint and the Lambda's execution role apply.

```
docker run -d -p 8000:8000 --name dynamodb-local amazon/dynamodb-local

aws dynamodb create-table \
  --table-name claim-games \
  --attribute-definitions AttributeName=Code,AttributeType=S \
  --key-schema AttributeName=Code,KeyType=HASH \
  --billing-mode PAY_PER_REQUEST \
  --endpoint-url http://localhost:8000 \
  --region eu-west-2
```

DynamoDB Local ignores credentials but the AWS CLI still wants some, so any dummy values in the
environment will do.

## Running locally

```
dotnet run --project ClaimBackend.Api
```

API docs (Scalar) are available at `/scalar/v1` in Development. Use the "Authorize" button
with a Cognito access token to call `/api/me` or `/api/hello`.

## Lambda deployment

The app hosts itself two ways from the same codebase, following the pattern used by
`OrderCometMonolith`:

- **Locally / normal container**: runs on Kestrel as usual (`dotnet run`, or the plain
  container image if one is added later).
- **AWS Lambda**: when the `AWS_LAMBDA_FUNCTION_NAME` environment variable is present (i.e.
  running inside Lambda), `Program.cs` calls
  `builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi)`, which swaps Kestrel for
  the Lambda Runtime API client. No controller/auth/DI code needs to change between the two.

`dockerfile-lambda` builds a container image on top of `public.ecr.aws/lambda/dotnet:10` for
this. Build and smoke-test it locally with the Lambda Runtime Interface Emulator baked into
the base image:

```
docker build -f dockerfile-lambda -t claim-backend-lambda .
docker run -p 9000:8080 claim-backend-lambda
curl -X POST http://localhost:9000/2015-03-31/functions/function/invocations \
  -d '{"version":"2.0","routeKey":"GET /api/health","rawPath":"/api/health","requestContext":{"http":{"method":"GET","path":"/api/health"}},"headers":{},"isBase64Encoded":false}'
```

This expects an API Gateway **HTTP API** (payload format 2.0) in front of it — that's what
`LambdaEventSource.HttpApi` above maps to.

### Infra & CI/CD

`claim-infrastructure/lambda.tf` provisions the ECR repo, the Lambda function, an HTTP API
Gateway in front of it, and the IAM role this repo's GitHub Actions workflow assumes.
The function is **not** attached to the default VPC yet — nothing queries RDS at runtime yet,
and a VPC-attached Lambda loses internet access (needed for Cognito's JWKS fetch) without a
NAT Gateway. See the comment block at the top of `lambda.tf` for the bootstrap order (the ECR
repo has to exist and have an image pushed before Terraform can create the Lambda function).

`.github/workflows/deploy.yml` builds `dockerfile-lambda`, pushes it to ECR tagged with both
the commit SHA and `latest`, then runs `aws lambda update-function-code` to point the function
at the new SHA-tagged image. It needs two repo secrets (not shared with `claim-frontend` —
GitHub secrets don't cross repos):

- `AWS_ROLE_ARN` — the `github_backend_deploy_role_arn` Terraform output
- `AWS_REGION` — `eu-west-2`
