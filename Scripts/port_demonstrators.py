
from pathlib import Path



src = Path("Demonstrators.Legacy")
dst = Path("RegressionTests")

for file in src.rglob("*.blade"):

    d = dst / file.relative_to(src)

    d.parent.mkdir(exist_ok=True)

    lines = file.read_text().splitlines()

    outlines = []

    eoh = False
    for line in lines:
        if not eoh and not line.lstrip().startswith("//"):
            
            outlines.append("cog task main {")
            eoh = True
        
        if eoh:
            line = "    " + line
        outlines.append(line)

    outlines.append("}")

    d.write_text("\n".join(outlines), 'utf-8')
