# SPDX-License-Identifier: MIT
PYTHON ?= /usr/bin/python3

.PHONY: all build build-app test test-app install install-app status config reset test-hid uninstall clean

all: build test test-app build-app

build:
	$(PYTHON) scripts/install.py build

build-app:
	$(PYTHON) scripts/install.py build-app

test:
	PYTHONPYCACHEPREFIX=/tmp/codex-kick75-pycache $(PYTHON) -m unittest discover -s tests -v

test-app:
	swift run --package-path macos-app CodexKick75CoreChecks

install:
	$(PYTHON) scripts/install.py install

install-app:
	$(PYTHON) scripts/install.py install-app

status:
	$(PYTHON) scripts/install.py status

config:
	$(PYTHON) scripts/install.py config

reset:
	$(PYTHON) scripts/install.py reset

test-hid:
	$(PYTHON) scripts/install.py test-hid

uninstall:
	$(PYTHON) scripts/install.py uninstall

clean:
	$(PYTHON) -c 'import pathlib, shutil; [shutil.rmtree(pathlib.Path(path), ignore_errors=True) for path in ("build", "macos-app/.build")]'
