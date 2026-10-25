import { Timeline } from "@/components/ui/timeline";

interface TimelineItem {
  id: string;
  title: string;
  description: string;
  timestamp: Date;
  status: "completed" | "active" | "pending";
}

export default function DemoOne() {
  const items: TimelineItem[] = [
    {
      id: "1",
      title: "Project Started",
      description: "Initial project setup and planning phase",
      timestamp: new Date("2024-01-15T09:00:00"),
      status: "completed",
    },
    {
      id: "2",
      title: "Development Phase",
      description: "Core features implementation in progress",
      timestamp: new Date("2024-02-01T10:30:00"),
      status: "active",
    },
    {
      id: "3",
      title: "Testing & QA",
      description: "Quality assurance and testing phase",
      timestamp: new Date("2024-02-15T14:00:00"),
      status: "pending",
    },
    {
      id: "4",
      title: "Launch",
      description: "Production deployment and launch",
      timestamp: new Date("2024-03-01T16:00:00"),
      status: "pending",
    },
  ];

  return <Timeline items={items} />;
}