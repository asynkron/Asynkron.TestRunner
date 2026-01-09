# Test262 Suite Structure

Discovered from: `Asynkron.JsEngine.Tests.Test262`

## Overview

- **Total Test Methods**: 1,631
- **Total Test Cases**: 92,945
- **Average Test Cases per Method**: ~57

## Largest Test Methods (by test case count)

| Rank | Method Name | Test Cases |
|------|-------------|------------|
| 1 | Expressions_class_dstr | 3,840 |
| 2 | Statements_class_dstr | 3,840 |
| 3 | Statements_forAwaitOf | 2,431 |
| 4 | Object_defineProperty | 2,250 |
| 5 | Statements_class_elements | 2,088 |
| 6 | Expressions_class_elements | 1,881 |
| 7 | Object_defineProperties | 1,264 |
| 8 | Expressions_object_dstr | 1,122 |
| 9 | Statements_forOf_dstr | 1,095 |
| 10 | RegExp | 976 |

## Structure

All tests are grouped under:
- **Namespace**: `Tests`
- **Class**: `GeneratedTests`
- **Methods**: 1,631 test methods with varying numbers of test cases

## Files

- `test262-structure.json` (881 KB) - Full test structure with all metadata
- `test262-top-methods.json` - Top 20 methods by test case count

## Batching Strategy

For efficient execution with hang detection:

1. **Method-level batching**: Group by test method (1,631 batches)
   - Smallest batch: ~1-2 test cases
   - Largest batch: 3,840 test cases
   - Most batches: ~50-60 test cases

2. **Adaptive splitting**: When a batch hangs:
   - Parse TRX to find passed tests
   - Parse blame-hang to find hanging test
   - Remove hanging test
   - Split remaining into smaller batches
   - Recurse until all non-hanging tests pass

3. **Tree display**: Show progress as:
   ```
   Tests > GeneratedTests > MethodName (X/Y test cases passed)
   ```
