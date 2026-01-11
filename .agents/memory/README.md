This is the memory of the agent. It contains information about important entities.
it contains small markdown files that describe important entities such as people, organizations, projects, and concepts.

Whenever you as an agent learn about a new important entity, you should create a new markdown file in this directory to describe it.
Each markdown file should start with a top-level heading that is the name of the entity, followed by a brief description and any relevant links to other entities in the memory.

In this format:

```md
# Roger Johansson
Owner of [file://asynkron-ab.md](Asynkron AB) and creator of [file://matcha.md](Matcha).
```

Whenever you learn about a relation between concepts, you should add links to the relevant markdown files using the format `[file://<filename>.md](<Entity Name>)` and descriptions of the relations.

You can use sub folders to organize the memory if it grows large, but keep the structure simple and easy to navigate.

e.g. You may have a subfolder for "architecture" and other abstract concepts