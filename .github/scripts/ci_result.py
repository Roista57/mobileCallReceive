"""Aggregate required jobs; skipped required builds are failures."""
import json
import os


def evaluate(needs):
    changes = needs['changes']
    errors = []
    if changes['result'] != 'success':
        errors.append('변경 경로 분석 실패 또는 취소')
    for platform in ('android', 'windows'):
        requested = changes.get('outputs', {}).get(platform)
        result = needs[platform]['result']
        if requested not in ('true', 'false'):
            errors.append(f'{platform}: 변경 분석 출력 누락 또는 오류')
        elif requested == 'true' and result != 'success':
            errors.append(f'{platform}: 필요한 빌드 결과가 {result}')
        elif requested == 'false' and result not in ('skipped', 'success'):
            errors.append(f'{platform}: 예상하지 않은 결과 {result}')
    return errors


def main():
    needs = json.loads(os.environ['CI_NEEDS'])
    errors = evaluate(needs)
    with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as summary:
        summary.write('### CI 결과\n\n')
        for platform in ('android', 'windows'):
            summary.write(f'- {platform}: {needs[platform]["result"]}\n')
        summary.write('\n' + ('\n'.join(errors) if errors else '필요한 검증이 모두 통과했습니다.') + '\n')
    if errors:
        raise SystemExit('\n'.join(errors))


if __name__ == '__main__':
    main()
