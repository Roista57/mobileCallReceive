import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from ci_changes import classify, detect
from ci_result import evaluate


class RulesTests(unittest.TestCase):
    def test_paths(self):
        cases = {
            'mobileCallReceive/app/src/main/Main.kt': {'android'},
            'CallReceiver/CallReceiver/App.cs': {'windows'},
            'version.properties': {'android', 'windows'},
            '.github/README.md': {'android', 'windows'},
            '.gitattributes': {'android', 'windows'},
            'README.md': set(), 'CallReceiver/README.md': set(),
            'mobileCallReceive/docs/image.png': set(), 'docs/example.txt': set(),
            'CallReceiver/docs/test.txt': set(), '.gitignore': set(),
            'Directory.Build.props': {'windows'}, 'Directory.Build.targets': {'windows'},
            'global.json': {'windows'}, 'NuGet.Config': {'windows'},
            'unknown.config': {'android', 'windows'},
        }
        for path, expected in cases.items():
            with self.subTest(path=path):
                self.assertEqual(expected, classify(path))

    def test_results(self):
        for selected in ('true', 'false'):
            for result in ('success', 'failure', 'cancelled', 'skipped'):
                with self.subTest(selected=selected, result=result):
                    needs = {'changes': {'result': 'success', 'outputs': {'android': selected, 'windows': 'false'}},
                             'android': {'result': result}, 'windows': {'result': 'skipped'}}
                    self.assertEqual(bool(evaluate(needs)), result != 'success' if selected == 'true'
                                     else result not in ('success', 'skipped'))
        for result in ('failure', 'cancelled', 'skipped', 'success'):
            self.assertTrue(evaluate({'changes': {'result': result, 'outputs': {}},
                                      'android': {'result': 'skipped'}, 'windows': {'result': 'skipped'}}))


class GitTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.previous = os.getcwd()
        os.chdir(self.temp.name)
        self.git('init', '-q')
        self.git('config', 'user.email', 'ci-test@example.invalid')
        self.git('config', 'user.name', 'CI test')
        self.base = self.commit('README.md', 'initial')

    def tearDown(self):
        os.chdir(self.previous)
        self.temp.cleanup()

    def git(self, *args):
        return subprocess.check_output(['git', *args], stderr=subprocess.PIPE).decode().strip()

    def commit(self, path, text='test'):
        file = Path(path)
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text(text)
        self.git('add', '-A')
        self.git('commit', '-qm', 'fixture')
        return self.git('rev-parse', 'HEAD')

    def push(self, base, head):
        return detect('push', {'before': base, 'after': head})[0]

    def test_docs_and_empty(self):
        head = self.commit('CallReceiver/README.md')
        self.assertEqual(set(), self.push(self.base, head))
        self.assertEqual(set(), self.push(head, head))

    def test_multiple_commits_spaces_and_docs(self):
        first = self.commit('mobileCallReceive/app/a b.kt')
        self.assertEqual({'android'}, self.push(self.base, first))
        self.commit('docs/guide.md')
        head = self.commit('CallReceiver/a.cs')
        self.assertEqual({'android', 'windows'}, self.push(self.base, head))

    def test_delete_and_cross_platform_rename(self):
        start = self.commit('mobileCallReceive/old file.txt')
        Path('CallReceiver').mkdir()
        self.git('mv', 'mobileCallReceive/old file.txt', 'CallReceiver/new file.txt')
        self.git('commit', '-qm', 'rename')
        renamed = self.git('rev-parse', 'HEAD')
        self.assertEqual({'android', 'windows'}, self.push(start, renamed))
        self.git('rm', 'CallReceiver/new file.txt')
        self.git('commit', '-qm', 'delete')
        self.assertEqual({'windows'}, self.push(renamed, self.git('rev-parse', 'HEAD')))

    def test_pr_uses_merge_base_and_all_pr_commits(self):
        self.git('checkout', '-qb', 'feature')
        self.commit('mobileCallReceive/a.kt')
        head = self.commit('docs/guide.md')
        self.git('checkout', '--detach', self.base)
        base_tip = self.commit('CallReceiver/unrelated.cs')
        event = {'pull_request': {'base': {'sha': base_tip}, 'head': {'sha': head}}}
        self.assertEqual({'android'}, detect('pull_request', event)[0])

    def test_missing_base_and_analysis_errors(self):
        for base in ('0' * 40, 'f' * 40, None):
            self.assertEqual({'android', 'windows'}, self.push(base, self.base))
        with self.assertRaises(ValueError):
            self.push(self.base, 'f' * 40)
        with patch('ci_changes.git', side_effect=RuntimeError('git failure')):
            with self.assertRaises(RuntimeError):
                self.push(self.base, self.base)


if __name__ == '__main__':
    unittest.main()
