# CASSINI Guardians of the Galaxy

**Civilian evacuation coordination for crisis management teams**

## What we are building

An application that helps a crisis coordinator understand who and what may be affected by a reported hazard, direct people out of the danger zone, dispatch assistance where needed, and update instructions as conditions change. Residents receive practical guidance through the app or from field teams.

## Who uses it

| Audience | What they receive |
| --- | --- |
| Crisis coordinators and local authorities | A map of relevant infrastructure, known and potential hazards, and available information about people in the affected area |
| Residents | A route out of the danger zone suited to their travel mode and mobility needs, or a way to request assistance |
| Dispatchers and field units | Assistance tasks, pickup details, and routes authorized for response units |

## MVP workflows

1. **Get the situation.** The client submits a hazard alert. The system returns a timestamped view of the affected area, relevant infrastructure, potential hazards, and available population observations or estimates.
2. **Route people to safety.** Propose a route to an eligible safe point outside the active danger zone. Account for closures, known traffic conditions, and the person's travel mode and access needs.
3. **Dispatch assistance.** Record requests from people who cannot leave independently and assign an eligible unit with a route to the person and, when needed, a route out.
4. **Monitor and adapt.** Track reported progress, closures and changes in hazard extent. Withdraw affected instructions and publish replacements when approved.

Shelter selection, reservations, and placement are outside the MVP. So is grouping people by household, family, caregiver, or other relationships. A person can still request help based on their own mobility and assistance needs.

## Role of EU space technologies

Copernicus EMS supplies timestamped hazard and damage layers where available. Road authorities and field teams supply current closure and traffic information. Galileo supports positioning on compatible devices. EGNOS can augment positioning where the receiver and service support it. IRIS² is a future option for connectivity at equipped coordination points. The MVP uses existing communications.

The project fits Challenge 2, *Space-enabled crisis response and civil resilience*. [CASSINI Participant Playbook, pp. 8 and 11–12](https://www.cassini.eu/hackathons/sites/default/files/2026-10/Participant%20Playbook_12th%20CASSINI%20Hackathon.pdf), [Copernicus EMS Rapid Mapping](https://mapping.emergency.copernicus.eu/about/rapid-mapping-portfolio/), [EUSPA Galileo FAQ](https://www.euspa.europa.eu/eu-space-programme/galileo/faqs), [EUSPA EGNOS FAQ](https://www.euspa.europa.eu/eu-space-programme/egnos/faqs), [European Commission IRIS² overview](https://defence-industry-space.ec.europa.eu/eu-space/iris2-secure-connectivity_en).

## Hackathon demonstration

A flood alert returns a situation view with an observed flood layer and simulated population and road inputs. A resident on foot receives a route out. A resident with limited mobility requests help and an eligible unit is dispatched. A bridge closure triggers withdrawal and recalculation for affected residents and units. The map shows who is confirmed outside the danger zone and who still needs follow-up.

Measure time from alert to the first situation view, time to assign assistance, time from closure to route withdrawal, and the number of cases with no confirmed exit.

Start with the [MVP implementation guide](mvp-implementation-guide.md). See also the [business processes](business-processes.md), [invariants](domain-invariants.md), [aggregates and service contracts](domain-architecture.md), [implementation work packages](mvp-work-packages.md), and [selected technology stack](mvp-technology-stack.md).
