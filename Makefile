COMPOSE ?= docker compose
COMPOSE_PROJECT ?= perene-tts
ARGS ?=
DOCKER_COMPOSE = $(COMPOSE) -p $(COMPOSE_PROJECT)

.PHONY: help docker-build docker-run docker-run-bg docker-down docker-reset docker-logs docker-ps docker-shell docker-exec dotnet docker-test docker-check get-url build up down logs ps test check mac-setup mac-worker mac-up mac-down desktop-mac desktop-dmg desktop-run desktop-smoke desktop-icon
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
		'Apple Metal: mac-setup, mac-worker, mac-up, mac-down' \
		'make desktop-mac        Build dist/Perene TTS.app and its zip in Docker (nothing installed on the Mac)' \
		'make desktop-dmg        Pack the built app into dist/Perene-TTS-<version>-macos-arm64.dmg (on the Mac)' \
		'make desktop-run        Open the built app (on the Mac)' \
		'make desktop-smoke      Offline check of the built app engine on the Mac (DEVICE=cpu|mps|auto)' \
		'make desktop-icon       Re-render the app icon and disk image background PNGs from their SVGs (Docker)'

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

# macOS desktop app (packaging/macos/). Built entirely inside Docker on any machine; the Mac only runs the result.
# The app is assembled and signed on the $(DESKTOP_BUILDER)-stage volume (rcodesign fails on macOS-shared folders), then copied to dist/.
DESKTOP_BUILDER ?= $(COMPOSE_PROJECT)-mac-builder
DESKTOP_APP = dist/Perene TTS.app
DEVICE ?= auto
desktop-mac:
	docker build --tag $(DESKTOP_BUILDER) packaging/macos
	docker run --rm -v "$(CURDIR):/src" -v $(DESKTOP_BUILDER)-stage:/stage -w /src $(DESKTOP_BUILDER) bash packaging/macos/build.sh $(STEPS)
desktop-dmg:
	bash packaging/macos/dmg.sh
desktop-run:
	open "$(DESKTOP_APP)"
desktop-smoke:
	@[ "$$(uname -s)" = Darwin ] || { echo 'make desktop-smoke runs the built app, so it needs the Mac.' >&2; exit 1; }
	@[ -x /usr/bin/sandbox-exec ] || [ "$(SMOKE_NO_SANDBOX)" = 1 ] || { echo 'sandbox-exec is unavailable: turn Wi-Fi off and rerun with SMOKE_NO_SANDBOX=1.' >&2; exit 1; }
	codesign --verify --deep --strict --verbose=1 "$(DESKTOP_APP)"
	SMOKE_DEVICE=$(DEVICE) SMOKE_NO_SANDBOX=$(SMOKE_NO_SANDBOX) "$(DESKTOP_APP)/Contents/Resources/python/bin/python3.11" -I packaging/macos/smoke.py "$(DESKTOP_APP)"
	@# Running must not write into the signed bundle (for example .pyc files from helper processes).
	codesign --verify --deep --strict --verbose=1 "$(DESKTOP_APP)"
desktop-icon:
	docker run --rm -v "$(CURDIR)/packaging/macos:/work" -w /work \
		-v "$(CURDIR)/WebApp/wwwroot/vendor/font-4.ttf:/usr/share/fonts/perene/ZillaSlab-SemiBold.ttf:ro" docker.io/library/alpine:3.20 \
		sh -c 'apk add --no-cache rsvg-convert fontconfig >/dev/null \
			&& { fc-list | grep -q "Zilla Slab SemiBold" || { echo "Zilla Slab SemiBold not found" >&2; exit 1; }; } \
			&& rsvg-convert -w 1024 -h 1024 perene-tts.svg -o AppIcon-1024.png \
			&& rsvg-convert -w 640 -h 400 dmg-background.svg -o dmg-background.png \
			&& rsvg-convert -w 1280 -h 800 dmg-background.svg -o dmg-background@2x.png'
