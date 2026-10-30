"""
Прописывает в PdmLite.sln явные зависимости сборки (ProjectDependencies) по ProjectReference
из .csproj. Visual Studio иногда не учитывает ссылки между проектами при параллельной сборке
(CS0006 «Metadata file … could not be found»); явные зависимости решения она соблюдает всегда.

Запуск из корня репозитория после добавления проекта или ссылки: python tools/sln-dependencies.py
"""
import os
import re
import sys

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sln_path = os.path.join(root, "PdmLite.sln")
text = open(sln_path, encoding="utf-8-sig").read()
nl = "\r\n" if "\r\n" in text else "\n"

project_re = re.compile(r'Project\("\{[^}]+\}"\) = "([^"]+)", "([^"]+\.csproj)", "(\{[^}]+\})"')
projects = {}  # абсолютный путь csproj → GUID
for name, rel, guid in project_re.findall(text):
    projects[os.path.normcase(os.path.normpath(os.path.join(root, rel.replace("\\", os.sep))))] = guid

def references(csproj):
    content = open(csproj, encoding="utf-8-sig").read()
    base = os.path.dirname(csproj)
    for ref in re.findall(r'<ProjectReference\s+Include="([^"]+)"', content):
        path = os.path.normcase(os.path.normpath(os.path.join(base, ref.replace("\\", os.sep))))
        if path in projects:
            yield projects[path]

# Убираем старые секции зависимостей и пишем заново.
text = re.sub(r"\tProjectSection\(ProjectDependencies\) = postProject" + re.escape(nl) + r".*?\tEndProjectSection" + re.escape(nl),
              "", text, flags=re.S)

def add_section(match):
    line = match.group(0)
    rel, guid = match.group(2), match.group(3)
    path = os.path.normcase(os.path.normpath(os.path.join(root, rel.replace("\\", os.sep))))
    deps = sorted(set(references(path)))
    if not deps:
        return line
    body = nl.join([f"\t\t{d} = {d}" for d in deps])
    return f"{line}{nl}\tProjectSection(ProjectDependencies) = postProject{nl}{body}{nl}\tEndProjectSection"

text = project_re.sub(add_section, text)
open(sln_path, "w", encoding="utf-8-sig", newline="").write(text)
print("Зависимости обновлены:", sum(1 for _ in re.finditer("ProjectSection\\(ProjectDependencies\\)", text)), "проектов")
