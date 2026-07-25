#!/usr/bin/env node
// SPDX-License-Identifier: MIT

import fs from "node:fs";

const bundlePaths = [
  "/private/tmp/nuphy-main.f6f60294.js",
  ...fs.readdirSync("/private/tmp/nuphy-chunks")
    .filter((name) => name.endsWith(".js"))
    .map((name) => `/private/tmp/nuphy-chunks/${name}`),
];

const sources = bundlePaths.map((path) => fs.readFileSync(path, "utf8"));
const moduleCache = new Map();
const factories = new Map();

function findModule(id) {
  if (factories.has(id)) return factories.get(id);
  const markers = [`${id}:function(`, `${id}:(`];
  for (const source of sources) {
    let marker = -1;
    for (const candidate of markers) {
      marker = source.indexOf(candidate);
      if (marker !== -1) break;
    }
    if (marker === -1) continue;

    const fnStart = source.indexOf("function", marker);
    const argsStart = source.indexOf("(", fnStart);
    const argsEnd = source.indexOf(")", argsStart);
    const args = source.slice(argsStart + 1, argsEnd).split(",");
    const bodyStart = source.indexOf("{", argsEnd);
    let depth = 0;
    let quote = null;
    let escaped = false;
    let lineComment = false;
    let blockComment = false;
    let bodyEnd = -1;

    for (let i = bodyStart; i < source.length; i += 1) {
      const ch = source[i];
      const next = source[i + 1];
      if (lineComment) {
        if (ch === "\n") lineComment = false;
        continue;
      }
      if (blockComment) {
        if (ch === "*" && next === "/") {
          blockComment = false;
          i += 1;
        }
        continue;
      }
      if (quote) {
        if (escaped) escaped = false;
        else if (ch === "\\") escaped = true;
        else if (ch === quote) quote = null;
        continue;
      }
      if (ch === "/" && next === "/") {
        lineComment = true;
        i += 1;
        continue;
      }
      if (ch === "/" && next === "*") {
        blockComment = true;
        i += 1;
        continue;
      }
      if (ch === '"' || ch === "'" || ch === "`") {
        quote = ch;
        continue;
      }
      if (ch === "{") depth += 1;
      if (ch === "}" && --depth === 0) {
        bodyEnd = i;
        break;
      }
    }
    if (bodyEnd === -1) throw new Error(`unterminated module ${id}`);
    let factory;
    try {
      factory = Function(...args, source.slice(bodyStart + 1, bodyEnd));
    } catch (error) {
      throw new Error(`failed compiling module ${id}: ${error.message}`, { cause: error });
    }
    factories.set(id, factory);
    return factory;
  }
  throw new Error(`module ${id} not found`);
}

const capturedClasses = [];
const parserClasses = [];
const opaque = new Proxy(function opaqueValue() { return opaque; }, {
  get() { return opaque; },
  construct() { return opaque; },
});

function webpackRequire(id) {
  if (moduleCache.has(id)) return moduleCache.get(id).exports;
  const module = { exports: {} };
  moduleCache.set(id, module);

  if ([62831, 97075, 89812, 44951].includes(id)) {
    module.exports = opaque;
    return module.exports;
  }

  if (id === 92901) {
    const real = loadNormally(id);
    module.exports = {
      A(ctor, prototypeDescriptors, staticDescriptors) {
        if (prototypeDescriptors?.some((item) => item.key === "getLightStates")) {
          capturedClasses.push({ ctor, prototypeDescriptors });
        }
        if (prototypeDescriptors?.some((item) => item.key === "parseFuncLightData")) {
          parserClasses.push({ ctor, prototypeDescriptors });
        }
        return real.A(ctor, prototypeDescriptors, staticDescriptors);
      },
    };
    return module.exports;
  }

  try {
    findModule(id)(module, module.exports, webpackRequire);
  } catch (error) {
    throw new Error(`failed loading module ${id}: ${error.message}`, { cause: error });
  }
  return module.exports;
}

function loadNormally(id) {
  const module = { exports: {} };
  findModule(id)(module, module.exports, webpackRequire);
  return module.exports;
}

webpackRequire.d = (exports, definitions) => {
  for (const [key, getter] of Object.entries(definitions)) {
    if (!Object.prototype.hasOwnProperty.call(exports, key)) {
      Object.defineProperty(exports, key, { enumerable: true, get: getter });
    }
  }
};
webpackRequire.r = (exports) => {
  Object.defineProperty(exports, "__esModule", { value: true });
};
webpackRequire.o = (object, property) =>
  Object.prototype.hasOwnProperty.call(object, property);
webpackRequire.n = (module) => {
  const getter = module?.__esModule ? () => module.default : () => module;
  webpackRequire.d(getter, { a: getter });
  return getter;
};
webpackRequire.p = "https://drive.nuphy.io/";

globalThis.self = globalThis;
globalThis.window = globalThis;
Object.defineProperty(globalThis, "navigator", {
  configurable: true,
  value: { language: "en-US" },
});
globalThis.document = {
  documentElement: { setAttribute() {} },
};
globalThis._cloneDeep = (value) => {
  if (value === undefined) return undefined;
  return structuredClone(value);
};
globalThis.localStorage = {
  getItem() { return null; },
  setItem() {},
  removeItem() {},
};

webpackRequire(86736);

const LIGHT_STATE_PAYLOAD = new Uint8Array([
  0x0b, 0x64, 0x01, 0x00, 0x00, 0x00, 0xff, 0xff, 0xff,
  0x00, 0x64, 0x01, 0x01, 0x00, 0xe9, 0xff, 0xfb,
]);

console.log(`captured ${capturedClasses.length} light API classes`);
for (const [index, item] of capturedClasses.entries()) {
  const keys = item.prototypeDescriptors.map((descriptor) => descriptor.key);
  console.log(`${index}: ${item.ctor.name}: ${keys.join(",")}`);
}

for (const [index, { ctor }] of capturedClasses.entries()) {
  const calls = [];
  const parserStub = {
    parseFuncLightData(...args) {
      console.log(`class ${index} parser args:`, args);
      return [];
    },
  };
  const target = Object.create(ctor.prototype);
  target.getDataPackage = async (...args) => {
    calls.push(args);
    return new Uint8Array(256);
  };
  Object.defineProperty(target, "handlers", { configurable: true, value: parserStub });
  const receiver = new Proxy(target, {
    get(object, property, proxy) {
      const value = Reflect.get(object, property, proxy);
      if (value === undefined) {
        console.log(`class ${index} missing property:`, String(property));
        return parserStub;
      }
      return value;
    },
  });
  try {
    const result = await receiver.getLightStates(0);
    console.log(`class ${index} calls:`, calls);
    console.log(`class ${index} result:`, result);
  } catch (error) {
    console.log(`class ${index} calls before error:`, calls);
    console.log(`class ${index} error:`, error?.stack || error);
  }
}

console.log(`captured ${parserClasses.length} light parser classes`);
for (const [index, { ctor, prototypeDescriptors }] of parserClasses.entries()) {
  console.log(
    `parser ${index}: ${ctor.name}: ${prototypeDescriptors.map((descriptor) => descriptor.key).join(",")}`,
  );
  const payload = LIGHT_STATE_PAYLOAD;
  try {
    const parser = new ctor();
    console.log(`parser ${index} decoded:`, parser.parseFuncLightData(payload, 0));
  } catch (error) {
    console.log(`parser ${index} decode error:`, error?.stack || error);
  }
}

if (capturedClasses[1] && parserClasses[1]) {
  const payload = LIGHT_STATE_PAYLOAD;
  const parser = new parserClasses[1].ctor();
  const states = parser.parseFuncLightData(payload);
  const target = Object.create(capturedClasses[1].ctor.prototype);
  const writes = [];
  target.getDataPackage = async () => payload;
  target.setDataPackage = async (...args) => {
    writes.push(args);
    return true;
  };
  Object.defineProperty(target, "lightDataHandler", {
    configurable: true,
    value: parser,
  });
  try {
    const result = await target.setLightState(0, "side", states[1]);
    console.log("side round-trip writes:", writes);
    console.log("side round-trip result:", result);
    writes.length = 0;
    const greenState = {
      ...states[1],
      mode: 2,
      brightness: 100,
      isRGB: false,
      color: "#00ff00",
    };
    const greenResult = await target.setLightState(0, "side", greenState);
    console.log("side static-green writes:", writes);
    console.log("side static-green result:", greenResult);
  } catch (error) {
    console.log("side round-trip error:", error?.stack || error);
  }
}

const lightConfigModule = webpackRequire(90355);
console.log("light config exports:", Object.keys(lightConfigModule));
for (const [key, value] of Object.entries(lightConfigModule)) {
  console.log(
    `light config ${key}:`,
    typeof value,
    Array.isArray(value) ? `array(${value.length})` : Object.keys(value || {}).slice(0, 20),
  );
}
const deviceEnums = webpackRequire(15860);
console.log("Kick75 enum:", deviceEnums.ui.Kick75);
console.dir(lightConfigModule.fe[deviceEnums.ui.Kick75], { depth: 8 });
