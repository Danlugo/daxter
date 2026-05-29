# DAXter — Docker-only build & run helpers.
IMAGE ?= daxter:latest

.PHONY: image test run save load clean

## Build the image (runs the test suite inside the build stage).
image:
	docker build -t $(IMAGE) .

## Run only the build+test stage (fails if any test fails).
test:
	docker build --target build -t daxter-build:latest .

## Run the CLI. Pass args via ARGS, e.g. make run ARGS='query "EVALUATE ROW(1,1)"'
run:
	./bin/daxter $(ARGS)

## Export the image to a shareable tarball (for passing it around).
save:
	docker save $(IMAGE) | gzip > daxter-image.tar.gz
	@echo "Wrote daxter-image.tar.gz — load it elsewhere with: make load"

## Load the image from the tarball.
load:
	docker load < daxter-image.tar.gz

## Remove the local image and build cache image.
clean:
	-docker image rm $(IMAGE) daxter-build:latest 2>/dev/null || true
