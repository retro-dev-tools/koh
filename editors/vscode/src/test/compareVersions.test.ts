import * as assert from 'assert';
import { compareVersions } from '../toolchain/ToolchainResolver';

suite('compareVersions', () => {
    test('orders by numeric parts, not lexical', () => {
        assert.ok(compareVersions('0.1.10', '0.1.9') > 0, '0.1.10 should rank above 0.1.9');
        assert.ok(compareVersions('0.2.0', '0.1.99') > 0);
    });

    test('equal versions return 0', () => {
        assert.strictEqual(compareVersions('1.2.3', '1.2.3'), 0);
    });

    test('release outranks its pre-release', () => {
        // Semver: 0.1.3 > 0.1.3-beta, so an update-prompt triggers
        // when someone has a "-beta" installed and 0.1.3 is published.
        assert.ok(compareVersions('0.1.3', '0.1.3-beta') > 0);
        assert.ok(compareVersions('0.1.3-beta', '0.1.3') < 0);
    });

    test('pre-release suffixes compare lexicographically', () => {
        assert.ok(compareVersions('0.1.3-beta.2', '0.1.3-beta.1') > 0);
        assert.ok(compareVersions('0.1.3-rc.1', '0.1.3-beta.1') > 0);
    });

    test('missing trailing parts are treated as 0', () => {
        assert.strictEqual(compareVersions('1.0', '1.0.0'), 0);
        assert.ok(compareVersions('1.1', '1.0.99') > 0);
    });
});
