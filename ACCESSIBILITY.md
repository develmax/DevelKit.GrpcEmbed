# Accessibility

GrpcEmbed is a .NET library, not an end-user graphical application. Our goal is to
make its documentation, examples, and contribution process usable by people with
different abilities and assistive technologies.

## Documentation and tooling

- Prefer readable text, descriptive links, and a logical heading structure.
- Provide text explanations for diagrams and meaningful alternative text for images.
- Keep essential instructions available as text, not only screenshots or color.
- Provide command-line build and test instructions in [CONTRIBUTING.md](CONTRIBUTING.md).

Repository pages are hosted by GitHub, which controls their navigation and visual
interface. Screen-reader, keyboard, contrast, and display behavior also depend on
the browser, terminal, IDE, and operating system being used.

## Limitations

We have not completed a formal accessibility audit and do not claim WCAG
certification. Generated API schemas and developer tooling can contain technical
structures that need additional explanation. Applications built with GrpcEmbed
remain responsible for their own user-interface accessibility.

## Report a barrier

Use the [accessibility issue form](https://github.com/develmax/DevelKit.GrpcEmbed/issues/new?template=accessibility_report.yml).
Include the affected page or tool, the task you were trying to complete, and what
would help. Browser, operating-system, and assistive-technology details are useful
but optional. Do not include personal medical information, credentials, or private
application data. Maintainers will investigate reports and discuss practical fixes;
there is no guaranteed response deadline.
