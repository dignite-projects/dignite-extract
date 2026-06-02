// Angular unit tests run on plain Vitest (jsdom) instead of the `@angular/build:unit-test`
// builder, whose browser-mode detection is defeated by devkit materializing the unset
// `browsers` option to `[]` (it checks `=== undefined`). Partial-compiled Angular/ABP
// libraries are JIT-linked at runtime via `@angular/compiler` here — no Angular Linker needed.
import '@angular/compiler';
import 'zone.js';
import 'zone.js/testing';

import { getTestBed } from '@angular/core/testing';
import {
  BrowserTestingModule,
  platformBrowserTesting,
} from '@angular/platform-browser/testing';

getTestBed().initTestEnvironment(BrowserTestingModule, platformBrowserTesting());
