COMPOSE ?= docker compose
COMPOSE_PROJECT ?= perene-tts
ARGS ?=
DOCKER_COMPOSE = $(COMPOSE) -p $(COMPOSE_PROJECT)

.PHONY: help docker-build docker-run docker-run-bg docker-down docker-reset docker-logs docker-ps docker-shell docker-exec dotnet docker-test docker-check get-url build up down logs ps test check mac-setup mac-worker mac-up mac-down
help:
	@printf '%s\n' \
		'make docker-build       Build frontend and TTS images' \
		'make docker-run         Run Blazor with hot reload and attached build/startup logs' \
		'make docker-run-bg      Run the same watch mode in the background' \
		'make docker-down        Stop containers, preserving saved voices and models' \
		'make docker-reset       Stop and DELETE saved voices, audio, models, and keys' \
		'make docker-logs        Follow web and TTS logs' \
		'make docker-ps          Show container status' \
		'make docker-shell       Open a new .NET SDK shell' \
		'make docker-exec        Open the running .NET SDK shell' \
		'make dotnet ARGS=build  Run a dotnet command in the SDK container' \
		'make test               Run worker pytest, web xUnit tests, and compile the frontend' \
		'make docker-check       Validate standard and Mac Compose configurations' \
		'make get-url            Show the published browser and worker URLs' \
		'Apple Metal: mac-setup, mac-worker, mac-up, mac-down'

docker-build:
	$(DOCKER_COMPOSE) build web tts
docker-run:
	$(DOCKER_COMPOSE) up --build web tts
docker-run-bg:
	$(DOCKER_COMPOSE) up --build --detach web tts
docker-down:
	$(DOCKER_COMPOSE) down --remove-orphans
docker-reset:
	$(DOCKER_COMPOSE) down --volumes --remove-orphans
docker-logs:
	$(DOCKER_COMPOSE) logs --follow web tts
docker-ps:
	$(DOCKER_COMPOSE) ps
docker-shell:
	$(DOCKER_COMPOSE) run --rm --no-deps --entrypoint /bin/sh web
docker-exec:
	$(DOCKER_COMPOSE) exec web /bin/sh
dotnet:
	$(DOCKER_COMPOSE) run --rm --no-deps --entrypoint dotnet web $(ARGS)
docker-test:
	$(DOCKER_COMPOSE) --profile test build tests
	$(DOCKER_COMPOSE) --profile test run --rm tests
	docker build --target web-test --tag $(COMPOSE_PROJECT)-web-test .
	docker build --target runtime --tag $(COMPOSE_PROJECT)-web-runtime .
docker-check:
	$(DOCKER_COMPOSE) config --quiet
	$(COMPOSE) -p $(COMPOSE_PROJECT)-mac -f docker-compose.mac.yml config --quiet
get-url:
	@echo 'Browser UI (published address):'
	@$(DOCKER_COMPOSE) port web 8080 | sed 's|^|http://|'
	@echo 'TTS worker health (published address):'
	@$(DOCKER_COMPOSE) port tts 5081 | sed 's|^|http://|; s|$$|/health|'

# Preserve the initial command names as aliases.
build: docker-build
up: docker-run-bg
down: docker-down
logs: docker-logs
ps: docker-ps
test: docker-test
check: docker-check

mac-setup:
	bash scripts/mac-worker.sh setup
mac-worker:
	bash scripts/mac-worker.sh run
mac-up:
	$(COMPOSE) -p $(COMPOSE_PROJECT)-mac -f docker-compose.mac.yml up --build --detach
mac-down:
	$(COMPOSE) -p $(COMPOSE_PROJECT)-mac -f docker-compose.mac.yml down --remove-orphans
