import { describe, expect, it } from "vitest";
import { mapHostRole } from "./identity";

describe("mapHostRole", () => {
  it("maps host admins to admin", () => {
    expect(mapHostRole("host.admin")).toBe("admin");
    expect(mapHostRole("HOST.ADMIN")).toBe("admin");
  });

  it("maps everything else to user", () => {
    expect(mapHostRole("host.user")).toBe("user");
    expect(mapHostRole(null)).toBe("user");
    expect(mapHostRole(undefined)).toBe("user");
  });
});
