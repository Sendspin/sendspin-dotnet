"""Bridge the PR #179 pair-method wire shape for the pinned old reference server."""

from pathlib import Path


def main() -> None:
    import aiosendspin.models.core as core

    path = Path(core.__file__)
    source = path.read_text()
    old_annotation = "    supported_pair_methods: list[PairMethodDescriptor] | None = None\n"
    new_annotation = (
        "    supported_pair_methods: list[PairMethodDescriptor] | dict[str, dict] | None = None\n"
    )
    if old_annotation not in source:
        raise RuntimeError(f"Unexpected aiosendspin schema in {path}")

    old_body = '''    def __post_init__(self) -> None:
        """Enforce that support configs match supported roles."""
        # Validate player role and support configuration
        # Require support objects only for the exact role version we parse (e.g. "player@v1").
        # Clients may advertise newer versions (e.g. "player@v2") which this server may not
        # implement. Those must not trigger v1 support requirements.
        unlisted: list[str] = []
'''
    new_body = """    def __post_init__(self) -> None:
        if isinstance(self.supported_pair_methods, dict):
            self.supported_pair_methods = [
                PairMethodDescriptor(method=method, **descriptor)
                for method, descriptor in self.supported_pair_methods.items()
            ]

        unlisted: list[str] = []
"""
    if old_body not in source:
        raise RuntimeError(f"Unexpected aiosendspin validation code in {path}")

    path.write_text(
        source.replace(old_annotation, new_annotation).replace(old_body, new_body),
    )


if __name__ == "__main__":
    main()
