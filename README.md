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
| POST   | `/api/games/{code}/join`  | Joins; re-joining is a no-op, not an error. Once started, only players who left can come back, onto their old team |
| POST   | `/api/games/{code}/leave` | Leaves; hands off the host role, deletes if empty and nobody left mid-game |
| POST   | `/api/games/{code}/start` | Host only; moves the game from `Lobby` to `InProgress` |

All of them require a Cognito access token.

### Playing

Each team holds five **claim** challenges. Completing one at an active borough claims it and
draws power cards — as many as the challenge says (`PowerDraws`, 1–5, default 2), of which the
team keeps one. The hot borough is worth an extra point, draws twice as many, and lets the team
keep two. A failed claim spends the card and draws nothing.

**Power cards** are **Steal**, built in (`Powers/PowerCatalogue.cs`), plus any **Curse** or
**Boost** a player writes in the app. Anyone can add, edit or delete those; they're honour
system — the app only records that one was played, and the team says so in the group chat. A
team holds at most five unplayed; played ones stay listed, marked as played. Player-written cards
live in the challenges table under a `POWER_CARD` partition; the original No tube card is written
there once, keeping its `no-tube` id. A game copies the wording of its cards when it starts.

Each game draws from one shared power deck, built from the **power card configuration** the
host picks in the lobby (a game can't start without one). Cards a team doesn't keep, or later
discards, go to a discard pile that is shuffled back in when the deck runs out; kept and played
cards never return, so "5 Steal" means at most five Steals in the whole game. The deck is copied
at the start, so editing a configuration never changes a game in progress.

Configurations are shared by everyone and anyone can edit them. They live in the challenges
table under their own `POWER_CONFIG` partition, so they need no extra infrastructure.

| Method | Route                                  | Notes                                               |
| ------ | -------------------------------------- | --------------------------------------------------- |
| GET    | `/api/power-configurations`            | All configurations, every card listed with its count |
| GET    | `/api/power-configurations/cards`      | Every card a configuration can include              |
| GET    | `/api/power-cards`                     | Every power card, Steal first                       |
| POST   | `/api/power-cards`                     | `{ kind: "Curse" \| "Boost", title, description }`  |
| PUT    | `/api/power-cards/{id}`                | Same body; Steal can't be changed                   |
| DELETE | `/api/power-cards/{id}`                | Configurations that counted it just drop it         |
| GET    | `/api/power-configurations/{id}`       |                                                     |
| POST   | `/api/power-configurations`            | `{ name, counts: { "steal": 5, "no-tube": 2 } }`     |
| PUT    | `/api/power-configurations/{id}`       | Same body; replaces the name and all counts         |
| POST   | `/api/games/{code}/power-configuration` | Host only, in the lobby: `{ configurationId }`     |

The board holds a few more boroughs than there are teams (teams + 4, between 4 and 10) unless
the host picks a size in the lobby.

A **curse** is cast on a team, who are notified and see it in the app. It can have a duration,
after which it lifts on its own; without one it lasts until the cursed team marks it cleared.
**Veto** and **Curse Reverse** are built in and never applied automatically — the targeted team
uses one on a curse while it's active. A Veto cancels it (or stops a steal on one of their
boroughs); a Reverse ends it and casts it afresh, full time, on the sender. A reversal can be
vetoed but not reversed again.

Playing a **Steal** at a borough another team holds reveals a random steal challenge and starts
its clock. Report the result before it runs out: success moves the borough, failure (or the
clock running out) moves nothing and the defending team draws the steal challenge's
`PowerDraws`, keeping one. Nothing is ever locked — rare steals are what stop boroughs
bouncing back and forth. A borough is hot at most once per game.

| Method | Route                                           | Notes                                       |
| ------ | ----------------------------------------------- | ------------------------------------------- |
| POST   | `/api/games/{code}/play`                        | Claim: `{ cardId, boroughId, succeeded }`   |
| POST   | `/api/games/{code}/powers/offers/{offerId}/keep` | `{ cardIds }`; empty turns the offer down  |
| POST   | `/api/games/{code}/powers/{cardId}/play`        | `{ boroughId }` for a Steal, `{ targetTeamId }` for a curse |
| POST   | `/api/games/{code}/powers/{cardId}/resolve`     | Steal only: `{ succeeded }`, before the clock runs out |
| POST   | `/api/games/{code}/powers/{cardId}/discard`     | Drops an unplayed card to make room         |
| POST   | `/api/games/{code}/curses/{curseId}/respond`    | `{ cardId }` — a Veto or Curse Reverse       |
| POST   | `/api/games/{code}/curses/{curseId}/clear`      | Untimed curses only, by the cursed team      |
| POST   | `/api/games/{code}/steals/{stealId}/veto`       | `{ cardId }` — a Veto, by the defending team |
| POST   | `/api/games/{code}/board-size`                  | Host only, lobby: `{ boardSize }`, null = auto |

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

### Where DynamoDB lives when running locally

By default the API talks to the **real** tables in AWS, using whatever credentials the AWS SDK
resolves. That means a game or challenge created while developing is the same one the deployed
app serves, so treat the data as shared rather than scratch.

Credentials come from the usual SDK chain, so pick the profile that can reach the account:

```
export AWS_PROFILE=claim-infra
dotnet run --project ClaimBackend.Api --launch-profile http
```

Without a working profile the SDK falls back to `default`, and calls fail with
`InvalidClientTokenId` rather than anything that names the real problem.

To work against a throwaway local database instead, set `Games:ServiceUrl` in
`appsettings.Development.json` and point it at DynamoDB Local. That setting configures the
shared DynamoDB client, so it governs challenges as well as games despite living under `Games`.

```
docker run -d -p 8000:8000 --name dynamodb-local amazon/dynamodb-local

aws dynamodb create-table --table-name claim-games \
  --attribute-definitions AttributeName=Code,AttributeType=S \
  --key-schema AttributeName=Code,KeyType=HASH \
  --billing-mode PAY_PER_REQUEST --endpoint-url http://localhost:8000 --region eu-west-2

aws dynamodb create-table --table-name claim-challenges \
  --attribute-definitions AttributeName=Kind,AttributeType=S AttributeName=CreatedAtId,AttributeType=S \
  --key-schema AttributeName=Kind,KeyType=HASH AttributeName=CreatedAtId,KeyType=RANGE \
  --billing-mode PAY_PER_REQUEST --endpoint-url http://localhost:8000 --region eu-west-2
```

DynamoDB Local keeps everything in memory, so stopping the container discards the data.

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
