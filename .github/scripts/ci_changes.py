"""Select CI jobs using the event's Git range. No third-party dependencies."""
import json
import os
from pathlib import Path
import re
import subprocess


def git(*args):
    return subprocess.check_output(['git', *args])


def has_commit(sha):
    if not re.fullmatch(r'[0-9a-fA-F]{40}', sha or ''):
        return False
    return subprocess.run(['git', 'cat-file', '-e', sha + '^{commit}'],
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0


def classify(path):
    if path.startswith('.github/') or path in ('version.properties', '.gitattributes'):
        return {'android', 'windows'}
    if (path.lower().endswith('.md') or path.startswith(('docs/', 'mobileCallReceive/docs/', 'CallReceiver/docs/'))
            or path == '.gitignore'):
        return set()
    if path in ('Directory.Build.props', 'Directory.Build.targets', 'global.json', 'NuGet.Config'):
        return {'windows'}
    if path.startswith('mobileCallReceive/'):
        return {'android'}
    if path.startswith('CallReceiver/'):
        return {'windows'}
    return {'android', 'windows'}


def detect(event_name, event):
    git('rev-parse', '--git-dir')  # Repository errors must fail, not silently skip builds.
    if event_name == 'push':
        base, head = event.get('before'), event['after']
    elif event_name == 'pull_request':
        pr = event['pull_request']
        base, head = pr['base']['sha'], pr['head']['sha']
    else:
        raise ValueError('Unsupported CI event: ' + event_name)
    if not has_commit(head):
        raise ValueError('Current commit is unavailable')
    if not has_commit(base):
        return {'android', 'windows'}, '비교 기준 커밋 없음: 두 플랫폼 실행'
    if event_name == 'pull_request':
        base = git('merge-base', base, head).decode().strip()
    # No rename detection: a move is a deletion plus addition, covering BOTH paths.
    raw = git('diff', '--name-only', '--no-renames', '-z', base, head, '--')
    paths = [p.decode('utf-8', errors='surrogateescape') for p in raw.split(b'\0') if p]
    selected = set().union(*(classify(p) for p in paths))
    return selected, f'변경 파일 {len(paths)}개를 경로 규칙으로 분석'


def main():
    event = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text(encoding='utf-8'))
    selected, reason = detect(os.environ['GITHUB_EVENT_NAME'], event)
    with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
        for platform in ('android', 'windows'):
            output.write(f'{platform}={str(platform in selected).lower()}\n')
    with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as summary:
        summary.write(f'### 변경 경로 분석\n\n{reason}\n\n')
        for platform in ('android', 'windows'):
            status = '실행: 영향받는 경로 또는 안전한 전체 실행' if platform in selected else '생략: 영향받는 경로 없음'
            summary.write(f'- {platform}: {status}\n')


if __name__ == '__main__':
    main()
