# CASSINI Guardians of the Galaxy

**Civilian evacuation coordination for crisis management teams**

## What we are building

A decision support application for mass evacuations during floods, wildfires, infrastructure failures, and other emergencies. It proposes who should leave, when they should depart, which route they can use, and where they can be received. A crisis coordinator reviews and authorizes the plan before instructions go out.

## Who it serves

| Audience | What they need from the application |
| --- | --- |
| Crisis coordinators and local authorities | One view of hazards, road capacity, shelter availability, and people awaiting help |
| Field teams and transport operators | Confirmed restrictions, pickup assignments, and the latest instructions |
| Shelter operators | Expected arrivals, remaining capacity, and the support each arriving group needs |
| Residents and caregivers | Clear instructions on when to leave, where to go, and how to request assistance through the app or a field team |

## Why it matters

People choosing their own routes can overload the same bridge or shelter. A route suitable for a car may be unusable for a pedestrian or someone using a wheelchair. Coordinating departures, routes, transport, and destinations together helps authorities identify those conflicts while there is still time to act. Municipalities and civil protection agencies are the intended deploying organizations.

## How it works

- **Plan by phase.** Set departure windows for each zone based on hazard exposure, time to clear the zone, and corridor capacity.
- **Match routes to people.** Plan for cars, pedestrians, and assisted transport. Account for reduced mobility, accessible pickup points, caregivers, and residents without a smartphone.
- **Reassign shelters as conditions change.** Account for people already admitted and groups still on the way. Propose new destinations when a shelter closes or loses capacity.
- **Monitor flows and simulate the next few hours.** Combine local observations and road status with mapped hazard areas. Compare scenarios for closures, queues, shelter arrivals, and unmet assistance needs before publishing revisions.

## Role of EU space technologies

| Technology | Proposed use |
| --- | --- |
| Copernicus EMS | Import timestamped hazard and damage layers, such as observed flood extent. Get current closures and congestion from road authorities, traffic feeds, or field reports. |
| Galileo | Use GNSS positions from compatible devices for navigation and, when shared, field position reports. Data transmission uses a separate connection. |
| EGNOS | Use augmentation where a compatible receiver and applicable service are available. |
| IRIS² | Explore future backhaul for equipped coordination points. The prototype uses terrestrial connectivity and models outages. |

The project fits Challenge 2, *Space-enabled crisis response and civil resilience*. See the [CASSINI Participant Playbook, pp. 8 and 11–12](https://www.cassini.eu/hackathons/sites/default/files/2026-10/Participant%20Playbook_12th%20CASSINI%20Hackathon.pdf), [Copernicus EMS Rapid Mapping](https://mapping.emergency.copernicus.eu/about/rapid-mapping-portfolio/), [EUSPA Galileo FAQ](https://www.euspa.europa.eu/eu-space-programme/galileo/faqs), [EUSPA EGNOS FAQ](https://www.euspa.europa.eu/eu-space-programme/egnos/faqs), and the [European Commission IRIS² overview](https://defence-industry-space.ec.europa.eu/eu-space/iris2-secure-connectivity_en).

## Hackathon demo and success measures

Demonstrate a flood scenario with two corridors, three shelters, pedestrians, cars, and a group requiring assisted transport. Close a bridge during the simulation and show which groups need new routes or destinations, what happens to the next departure phase, and which instructions still need delivery. Use a Copernicus hazard layer and clearly labeled simulated traffic, occupancy, and closure events.

Compare the coordinated plan with a same-scenario baseline where groups depart together and choose their nearest eligible shelter. Report how many people remain in affected zones at key times, the peak corridor queue, the time to clear each zone, and how long people wait for assisted pickup.

See the [business processes](../domain/business-processes.md) and [domain invariants](../domain/domain-invariants.md).
