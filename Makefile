# SPDX-License-Identifier: MIT
PYTHON ?= /usr/bin/python3

.PHONY: all build test install status reset test-hid uninstall clean

all: build test

build:
	$(PYTHON) scripts/install.py build

test:
	PYTHONPYCACHEPREFIX=/tmp/codex-kick75-pycache $(PYTHON) -m unittest discover -s tests -v

install:
	$(PYTHON) scripts/install.py install

status:
	$(PYTHON) scripts/install.py status

reset:
	$(PYTHON) scripts/install.py reset

test-hid:
	$(PYTHON) scripts/install.py test-hid

uninstall:
	$(PYTHON) scripts/install.py uninstall

clean:
	$(PYTHON) -c 'import pathlib, shutil; shutil.rmtree(pathlib.Path("build"), ignore_errors=True)'
